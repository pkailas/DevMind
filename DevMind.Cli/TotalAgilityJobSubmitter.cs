#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind.Cli;

/// <summary>
/// Submits a sanitized PDF to Tungsten TotalAgility 2026.2 via its HTTP-JSON (REST) web API
/// and creates a job. Uses HttpClient POST with JSON body — no SDK proxy assemblies.
///
/// ============================================================
/// LIBRARY CITATIONS (TungstenTotalAgilitySDK_2026.2 docs):
/// ============================================================
///
/// WIRED FORMAT — from Samples.md §4 (worked jQuery AJAX samples):
///
/// 1. ENDPOINT URL PATTERN (Samples.md §4, LogOn sample line 34):
///    "http://localhost/TotalAgility/Services/Sdk/UserService.svc/json/LogOnWithPassword2"
///    Pattern: {baseUrl}/Services/Sdk/{ServiceName}.svc/json/{MethodName}
///
/// 2. LOGON REQUEST BODY (Samples.md §4, lines 22-26):
///    Top-level key = SDK parameter name in camelCase: "userIdentityWithPassword"
///    Nested object uses PascalCase SDK property names:
///      { "userIdentityWithPassword": { "UserId": "...", "Password": "...",
///        "LogOnProtocol": "7", "UnconditionalLogOn": true } }
///    Note: LogOnProtocol is sent as a STRING in the sample (line 23: "7"), not a number.
///
/// 3. LOGON RESPONSE (Samples.md §4, line 40):
///    jQuery AJAX reads result.d — the .d wrapper is standard ASP.NET AJAX JSON wrapping.
///    For plain HttpClient (no jQuery), the response body is the unwrapped JSON object
///    directly (the .d is added by jQuery's dataType:"json" handling of ASP.NET AJAX).
///    Session2 fields from Services.md §235: SessionId, ResourceId, DisplayName,
///    isValid, ReserveLicenseUsed, LogonState.
///
/// 4. LOGOFF REQUEST (Samples.md §4, lines 10-12, 18, 26):
///    Body: { "sessionId": "B1B44E3109E6447BB4EEBA578937402B" }
///    Endpoint: Services/Sdk/UserService.svc/json/LogOff
///    SDK: UserService.LogOff(string sessionId) → void (Services.md §232)
///
/// 5. CREATE JOB WITH DOCUMENTS:
///    Two sample variants found — we use CreateJobWithDocumentsAndProgress2
///    (the richer variant that returns progress info).
///
///    a) CreateJobWithDocumentsAndProgress2 (Samples.md §4-5, lines 11-26, 86):
///       Endpoint: Services/Sdk/JobService.svc/json/CreateJobWithDocumentsAndProgress2
///       Request body keys (camelCase parameter names):
///         "sessionId", "processIdentity", "jobWithDocsInitialization", "variablesToReturn"
///       jobWithDocsInitialization fields:
///         "InputVariables", "RuntimeDocumentCollection", "StartDate"
///       RuntimeDocument fields (Samples.md §4, lines 29-47):
///         "Base64Data" (string, alternate to Data), "Data" (byte[] as JS array),
///         "MimeType" (required), "FilePath" (server-side path, alternate to Data),
///         "DocumentTypeName", "DocumentTypeId", "ReturnAllFields", "RuntimeFields", etc.
///       Response: result.d (line 92) — JobWithDocumentsProgressOutput2 with JobIdentity, JobStatus
///
///    b) CreateJobSyncWithDocuments (Samples.md §5, lines 11-16):
///       Uses "Documents" collection (not "RuntimeDocumentCollection").
///       This is a DIFFERENT SDK method with a DIFFERENT collection name.
///
///    We choose CreateJobWithDocumentsAndProgress2 because it is the documented
///    async variant that accepts RuntimeDocumentCollection with Base64Data.
///
/// 6. PROPERTY CASING (observed across all samples):
///    - Top-level keys (SDK parameter names): camelCase → sessionId, processIdentity,
///      jobWithDocsInitialization, variablesToReturn, userIdentityWithPassword
///    - Nested object properties: PascalCase → UserId, Password, SessionId,
///      Base64Data, MimeType, InputVariables, RuntimeDocumentCollection
///    - This matches the SDK's .NET property names being serialized with default casing
///      for nested objects, while parameter names are camelCase per ASP.NET AJAX convention.
///
/// ============================================================
/// ASSUMPTIONS (NOT in the samples or reference docs):
/// ============================================================
/// - HttpClient construction (standard .NET, no special headers needed beyond
///   Content-Type: application/json; charset=utf-8)
/// - The base URL path (e.g. "https://server/TotalAgility") — the sample uses
///   "http://localhost/TotalAgility" (line 34)
/// - For plain HttpClient (no jQuery), the response is unwrapped JSON (no .d key).
///   The .d wrapper is added by ASP.NET AJAX for jQuery clients only.
/// - CreateJobWithDocumentsAndProgress2 response shape for plain HttpClient:
///   the raw JobWithDocumentsProgressOutput2 JSON object with JobIdentity.Id
/// - The server is reachable and the process/document type names are valid
/// </summary>
public sealed class TotalAgilityJobSubmitter : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private bool _disposed;

    /// <summary>
    /// Creates a submitter for the specified TotalAgility server.
    /// </summary>
    /// <param name="baseUrl">Base URL including app name, e.g. "https://server/TotalAgility".</param>
    /// <param name="httpClient">Optional; a new HttpClient is created if null.</param>
    public TotalAgilityJobSubmitter(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    /// <summary>
    /// Submits a PDF to TotalAgility and creates a job.
    /// Flow: LogOn → CreateJobWithDocumentsAndProgress2 → LogOff.
    /// </summary>
    /// <param name="userName">TotalAgility user name.</param>
    /// <param name="password">TotalAgility password.</param>
    /// <param name="processName">Name of the process map.</param>
    /// <param name="documentTypeName">Name of the document type for the PDF.</param>
    /// <param name="pdfFilePath">Server-readable file path (or null to use pdfBytes).</param>
    /// <param name="pdfBytes">Raw PDF bytes (or null to read from pdfFilePath).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created job ID.</returns>
    public async Task<string> SubmitPdfAsync(
        string userName,
        string password,
        string processName,
        string documentTypeName,
        string? pdfFilePath = null,
        byte[]? pdfBytes = null,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Log on — UserService.LogOnWithPassword2
        // SDK: Services.md §235
        // Sample: Samples.md §4, line 34
        string sessionId = await LogOnAsync(userName, password, cancellationToken);

        try
        {
            // Step 2: Create job with document — JobService.CreateJobWithDocumentsAndProgress2
            // SDK: Services.md §125
            // Sample: Samples.md §4-5, lines 11-26, 86
            string jobId = await CreateJobWithDocumentsAsync(
                sessionId, processName, documentTypeName, pdfFilePath, pdfBytes, cancellationToken);

            // Step 3: Log off — UserService.LogOff
            // SDK: Services.md §232
            // Sample: Samples.md §4, line 26
            await LogOffAsync(sessionId, cancellationToken);

            return jobId;
        }
        catch
        {
            try { await LogOffAsync(sessionId, cancellationToken); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Logs on and returns the session ID.
    ///
    /// Endpoint (Samples.md §4, line 34):
    ///   Services/Sdk/UserService.svc/json/LogOnWithPassword2
    ///
    /// Request body (Samples.md §4, lines 22-26):
    ///   { "userIdentityWithPassword": { "UserId": "...", "Password": "...",
    ///     "LogOnProtocol": "7", "UnconditionalLogOn": true } }
    ///
    /// Response (Samples.md §4, line 40): result.d → Session2 with SessionId
    ///   For plain HttpClient: raw JSON { "SessionId": "...", ... }
    /// </summary>
    private async Task<string> LogOnAsync(
        string userName, string password, CancellationToken cancellationToken)
    {
        // Build request body matching Samples.md §4, lines 22-26.
        // Top-level key: "userIdentityWithPassword" (camelCase SDK parameter name).
        // Nested properties: PascalCase SDK field names (UserId, Password, LogOnProtocol, UnconditionalLogOn).
        // LogOnProtocol is sent as a STRING per the sample (line 23: "7").
        var body = new
        {
            userIdentityWithPassword = new
            {
                UserId = userName,
                Password = password,
                LogOnProtocol = "7",          // string per sample line 23
                UnconditionalLogOn = true
            }
        };

        string json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json; charset=utf-8");

        // Endpoint from Samples.md §4, line 34:
        //   "http://localhost/TotalAgility/Services/Sdk/UserService.svc/json/LogOnWithPassword2"
        var response = await _httpClient.PostAsync(
            "Services/Sdk/UserService.svc/json/LogOnWithPassword2",
            content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        // Assumption: plain HttpClient gets unwrapped JSON (no .d key).
        // Session2 fields from Services.md §235: SessionId, ResourceId, DisplayName, isValid, ...
        using JsonDocument doc = JsonDocument.Parse(responseText);
        return doc.RootElement.GetProperty("SessionId").GetString()!;
    }

    /// <summary>
    /// Logs off the session.
    ///
    /// Endpoint (Samples.md §4, line 26):
    ///   Services/Sdk/UserService.svc/json/LogOff
    ///
    /// Request body (Samples.md §4, lines 10-12, 18):
    ///   { "sessionId": "B1B44E3109E6447BB4EEBA578937402B" }
    /// </summary>
    private async Task LogOffAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Body from Samples.md §4, lines 10-12, 18: { "sessionId": "..." }
        var body = new { sessionId };
        string json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json; charset=utf-8");

        // Endpoint from Samples.md §4, line 26:
        //   "http://localhost/TotalAgility/Services/Sdk/UserService.svc/json/LogOff"
        var response = await _httpClient.PostAsync(
            "Services/Sdk/UserService.svc/json/LogOff",
            content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates a job with the PDF document attached.
    ///
    /// Uses CreateJobWithDocumentsAndProgress2 (Samples.md §4-5, lines 11-26, 86).
    /// Endpoint: Services/Sdk/JobService.svc/json/CreateJobWithDocumentsAndProgress2
    ///
    /// Request body structure (Samples.md §4, lines 11-26):
    ///   {
    ///     "sessionId": "...",
    ///     "processIdentity": { "Id": "", "Name": "", "Version": 0.0 },
    ///     "jobWithDocsInitialization": {
    ///       "InputVariables": null,
    ///       "RuntimeDocumentCollection": [ ... ],
    ///       "StartDate": null
    ///     },
    ///     "variablesToReturn": []
    ///   }
    ///
    /// RuntimeDocument fields (Samples.md §4, lines 29-47):
    ///   Base64Data (string, alternate to Data), Data (byte[] as JS array),
    ///   MimeType (required), FilePath (server-side path), DocumentTypeName,
    ///   ReturnAllFields, RuntimeFields, ...
    ///
    /// Response (Samples.md §5, line 92): result.d → JobWithDocumentsProgressOutput2
    ///   Contains JobIdentity with Id field.
    /// </summary>
    private async Task<string> CreateJobWithDocumentsAsync(
        string sessionId,
        string processName,
        string documentTypeName,
        string? pdfFilePath,
        byte[]? pdfBytes,
        CancellationToken cancellationToken)
    {
        // Resolve PDF content
        byte[] documentData = pdfBytes ?? File.ReadAllBytes(pdfFilePath!);
        string base64Data = Convert.ToBase64String(documentData);

        // Build request body matching Samples.md §4, lines 11-47.
        // Top-level keys: camelCase SDK parameter names (sessionId, processIdentity,
        //   jobWithDocsInitialization, variablesToReturn).
        // Nested properties: PascalCase SDK field names.
        //
        // RuntimeDocumentCollection from line 15: "RuntimeDocumentCollection": []
        // RuntimeDocument fields from lines 29-47:
        //   Base64Data (string) — alternate to Data, used here for PDF
        //   MimeType (required per line 41 comment)
        //   FilePath (server-side path, line 38) — alternate to Data
        //   DocumentTypeName (line 36) — for identifying the document type
        //   ReturnAllFields (line 44) — set true per sample line 71
        var body = new
        {
            sessionId,
            processIdentity = new
            {
                Id = (string?)null,           // null = use Name to resolve (sample line 19-21)
                Name = processName,
                Version = 0.0
            },
            jobWithDocsInitialization = new
            {
                InputVariables = (object?)null,
                RuntimeDocumentCollection = new[]
                {
                    new
                    {
                        Base64Data = base64Data,
                        Data = (object?)null,
                        MimeType = "application/pdf",
                        FilePath = pdfFilePath,
                        DocumentTypeName = documentTypeName,
                        DocumentTypeId = (string?)null,
                        DeleteDocument = false,
                        DocumentGroup = (object?)null,
                        FieldsToReturn = new object[0],
                        FolderId = (string?)null,
                        FolderTypeId = (string?)null,
                        PageDataList = new object[0],
                        PageImages = new object[0],
                        ReturnAllFields = true,
                        RuntimeFields = new object[0]
                    }
                },
                StartDate = (object?)null
            },
            variablesToReturn = new object[0]
        };

        string json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json; charset=utf-8");

        // Endpoint from Samples.md §5, line 86:
        //   "http://localhost/TotalAgility/Services/Sdk/JobService.svc/json/CreateJobWithDocumentsAndProgress2"
        var response = await _httpClient.PostAsync(
            "Services/Sdk/JobService.svc/json/CreateJobWithDocumentsAndProgress2",
            content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        // Assumption: plain HttpClient gets unwrapped JSON.
        // JobWithDocumentsProgressOutput2 contains JobIdentity.Id
        using JsonDocument doc = JsonDocument.Parse(responseText);
        return doc.RootElement
            .GetProperty("JobIdentity")
            .GetProperty("Id")
            .GetString()!;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _httpClient.Dispose();
        _disposed = true;
    }
}
