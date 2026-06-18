// File: SqlExecutor.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Shared Core helper for executing read-only SQL queries.
// Used by both ConsoleAgenticHost and TuiAgenticHost.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DevMind
{
    /// <summary>
    /// Shared Core helper for executing read-only SQL queries and formatting results.
    /// Read-only guard, transaction rollback, connection string masking all enforced here.
    /// </summary>
    public static class SqlExecutor
    {
        private static readonly string[] _writeKeywords =
        { "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC" };

        /// <summary>
        /// Executes a SQL query with read-only guard and returns formatted results.
        /// </summary>
        /// <param name="query">SQL query to execute.</param>
        /// <param name="connectionString">Database connection string (never logged).</param>
        /// <param name="allowWrite">If true, skip the read-only guard.</param>
        /// <param name="maxRows">Maximum rows to return (0 = use all).</param>
        /// <param name="commandTimeout">Command timeout in seconds.</param>
        /// <returns>Formatted text table or error message.</returns>
        public static string ExecuteQuery(
            string query,
            string connectionString,
            bool allowWrite,
            int maxRows,
            int commandTimeout,
            out bool connectionOpened)
        {
            // Reports whether the connection actually opened, so the caller can cache a
            // known-good connection for the session even if the query itself later errors.
            connectionOpened = false;

            if (string.IsNullOrWhiteSpace(query))
                return "[ERROR] Empty query.";

            if (string.IsNullOrWhiteSpace(connectionString))
                return "[ERROR] No connection string available.";

            // Read-only guard
            if (!allowWrite)
            {
                var trimmed = query.Trim();
                var upper = trimmed.ToUpperInvariant();

                // Check for multiple statements (semicolon outside string literals)
                if (trimmed.Contains(';'))
                    return "[ERROR] Multiple statements are not allowed in read-only mode. Use a single SELECT statement.";

                // Must start with SELECT or WITH (CTE)
                if (!upper.StartsWith("SELECT") && !upper.StartsWith("WITH"))
                    return "[ERROR] Read-only mode: only SELECT statements are allowed.";

               // Check for write keywords anywhere in the full statement (not just leading keyword).
                // Scan the entire uppercased query for whole-word DML keywords.
                foreach (var kw in _writeKeywords)
                {
                    // EXEC/sp_executesql: check for EXEC/EXECUTE/SP_EXECUTESQL as whole words
                    if (kw == "EXEC")
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(upper, @"\bEXEC\b") ||
                            System.Text.RegularExpressions.Regex.IsMatch(upper, @"\bEXECUTE\b") ||
                            System.Text.RegularExpressions.Regex.IsMatch(upper, @"\bSP_EXECUTESQL\b"))
                            return "[ERROR] Read-only mode: EXEC/EXECUTE/SP_EXECUTESQL is not allowed.";
                    }
                    // SELECT...INTO
                    else if (kw == "INSERT")
                    {
                        if (upper.Contains("SELECT") && upper.Contains("INTO"))
                            return "[ERROR] Read-only mode: SELECT...INTO is not allowed.";
                    }
                    // All other write keywords: whole-word match anywhere in the statement
                    else
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(upper, $@"\b{kw}\b"))
                            return "[ERROR] Read-only mode: only SELECT statements are allowed.";
                    }
                }
            }

            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();
                connectionOpened = true; // connection is good — caller may cache it for the session

                using var transaction = connection.BeginTransaction();
                try
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = query;
                    command.CommandTimeout = commandTimeout;

                    var rows = new List<List<string>>();
                    var columnNames = new List<string>();
                    int totalRows = 0;

                    // Materialize all rows into memory, then close the reader BEFORE the rollback.
                    // A SqlDataReader left open on the connection blocks transaction.Rollback()
                    // ("There is already an open DataReader associated with this Connection..."),
                    // so the reader must be disposed first. The scoped using closes it at the end
                    // of this block — do NOT widen it to `using var` (which would defer disposal
                    // past the rollback), and do NOT enable MARS to mask the lifecycle.
                    using (var reader = command.ExecuteReader())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            columnNames.Add(reader.GetName(i));

                        while (reader.Read())
                        {
                            if (maxRows > 0 && rows.Count >= maxRows)
                            {
                                totalRows++; // count truncated rows
                                // skip remaining
                                while (reader.Read()) totalRows++;
                                break;
                            }
                            totalRows++;
                            var row = new List<string>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var val = reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString() ?? "(null)";
                                // Cap cell width at 80 chars
                                if (val.Length > 80) val = val.Substring(0, 77) + "...";
                                row.Add(val);
                            }
                            rows.Add(row);
                        }
                    } // reader closed/disposed here, before the rollback below

                    // Rollback transaction (always, since we're read-only) — reader is now closed.
                    transaction.Rollback();

                    // Format as text table (rows already materialized in memory)
                    return FormatTable(columnNames, rows, totalRows, maxRows);
                }
                catch
                {
                    // Attempt rollback on error
                    try { transaction.Rollback(); } catch { /* ignore */ }
                    throw;
                }
            }
            catch (SqlException ex)
            {
                return $"[SQL ERROR] {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"[ERROR] {ex.Message}";
            }
        }

        /// <summary>
        /// Formats query results as a compact text table.
        /// </summary>
        private static string FormatTable(List<string> columns, List<List<string>> rows, int totalRows, int maxRows)
        {
            if (columns == null || columns.Count == 0)
                return "[No columns returned]";

            var sb = new StringBuilder();

            // Calculate column widths
            var widths = new int[columns.Count];
            for (int i = 0; i < columns.Count; i++)
                widths[i] = columns[i].Length;

            foreach (var row in rows)
                for (int i = 0; i < row.Count && i < columns.Count; i++)
                    widths[i] = Math.Max(widths[i], row[i].Length);

            // Header + separator. The separator mirrors the header cell layout: each column gets a
            // dashed segment of (content width + 2 padding spaces), bracketed by pipes -> |---|---|.
          var header = new StringBuilder();
            var separator = new StringBuilder();
            header.Append("| ");
            separator.Append("| ");
            for (int i = 0; i < columns.Count; i++)
            {
                var cell = columns[i].PadRight(widths[i]);
                header.Append(cell).Append(" | ");
                separator.Append(new string('-', widths[i] + 2)).Append(" | ");
            }
            sb.AppendLine(header.ToString());
            sb.AppendLine(separator.ToString());

            // Rows
            foreach (var row in rows)
            {
                var line = new StringBuilder();
                line.Append("| ");
                for (int i = 0; i < columns.Count; i++)
                {
                    var val = i < row.Count ? row[i] : "";
                    line.Append(val.PadRight(widths[i])).Append(" | ");
                }
                sb.AppendLine(line.ToString());
            }

            // Row count
            if (maxRows > 0 && totalRows > maxRows)
                sb.AppendLine($"Showing {maxRows} of {totalRows} rows (truncated).");
            else
                sb.AppendLine($"{totalRows} row{(totalRows == 1 ? "" : "s")}.");

            return sb.ToString();
        }

        /// <summary>
        /// Resolves the connection string to use, in precedence order:
        ///   1. <paramref name="explicitConnectionString"/> — used verbatim (overrides everything);
        ///   2. named connection <paramref name="connectionName"/> from <paramref name="namedConnections"/>
        ///      (devmind.json sqlConnections). When no name is given: DefaultConnection, then "default", then the first usable entry;
        ///   3. <paramref name="sessionConnectionString"/> — the last connection that opened successfully this session (sticky reuse);
        ///   4. appsettings*.json in <paramref name="workingDirectory"/> (ConnectionStrings:DefaultConnection, then the first found).
        /// Returns <c>null</c> and sets <paramref name="error"/> — naming the sources it tried — when none resolve.
        /// The returned string may carry a SQL-auth password; callers MUST run it through
        /// <see cref="MaskConnectionString"/> before logging or echoing, and must never persist it.
        /// </summary>
        public static string ResolveConnectionString(
            string explicitConnectionString,
            string connectionName,
            IReadOnlyDictionary<string, string> namedConnections,
            string sessionConnectionString,
            string workingDirectory,
            out string error)
        {
            error = null;

            // 1. Explicit inline connection string — highest precedence, used as-is.
            if (!string.IsNullOrWhiteSpace(explicitConnectionString))
                return explicitConnectionString;

            // 2. Named connection from devmind.json sqlConnections (a specific name was requested).
            if (!string.IsNullOrWhiteSpace(connectionName))
            {
                if (namedConnections != null
                    && namedConnections.TryGetValue(connectionName, out var named)
                    && !string.IsNullOrWhiteSpace(named))
                    return named;

                // Named connection requested but not found — fail rather than silently falling back.
                error =
                    "No connection string available. Tried, in order: " +
                    "(1) explicit connection_string [not provided]; " +
                    $"(2) named connection '{connectionName}' in devmind.json sqlConnections [not found]; " +
                    "(3) appsettings*.json in the working directory [skipped — a specific connection_name was requested]. " +
                    "Add the named connection to devmind.json sqlConnections, pass connection_string inline, or omit connection_name to use a default.";
                return null;
            }

            // 2b. No name given — DefaultConnection, then "default", then the first usable entry,
            //     from named connections. ("default" gives a cross-session reuse hook in devmind.json.)
            if (namedConnections != null && namedConnections.Count > 0)
            {
                if (namedConnections.TryGetValue("DefaultConnection", out var defConn) && !string.IsNullOrWhiteSpace(defConn))
                    return defConn;

                if (namedConnections.TryGetValue("default", out var defConn2) && !string.IsNullOrWhiteSpace(defConn2))
                    return defConn2;

                foreach (var kvp in namedConnections)
                    if (!string.IsNullOrWhiteSpace(kvp.Value))
                        return kvp.Value;
            }

            // 3. Session sticky connection — the last connection that opened successfully this session.
            if (!string.IsNullOrWhiteSpace(sessionConnectionString))
                return sessionConnectionString;

            // 4. appsettings*.json in the working directory.
            var fromAppSettings = TryResolveFromAppSettings(workingDirectory);
            if (!string.IsNullOrWhiteSpace(fromAppSettings))
                return fromAppSettings;

            // 5. Nothing resolved — name every source tried so the failure is diagnosable.
            var namedState = (namedConnections == null || namedConnections.Count == 0)
                ? "none configured"
                : "no usable entry (no DefaultConnection/default or non-empty value)";
            var appSettingsState = string.IsNullOrEmpty(workingDirectory)
                ? "no working directory"
                : "no appsettings*.json with a ConnectionStrings entry";
            error =
                "No connection string available. Tried, in order: " +
                "(1) explicit connection_string [not provided]; " +
                $"(2) named connections in devmind.json sqlConnections [{namedState}]; " +
                "(3) session sticky connection [none cached yet — run one query with a connection first]; " +
                $"(4) appsettings*.json in the working directory [{appSettingsState}]. " +
                "Provide connection_string inline, add a connection to devmind.json sqlConnections, " +
                "or place an appsettings.json with ConnectionStrings:DefaultConnection in the working directory.";
            return null;
        }

        /// <summary>
        /// Reads a connection string from appsettings.Development.json / appsettings.json in
        /// <paramref name="workingDirectory"/> (ConnectionStrings:DefaultConnection, then the first entry).
        /// Returns <c>null</c> when nothing usable is found. Malformed config files are skipped.
        /// </summary>
        private static string TryResolveFromAppSettings(string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory))
                return null;

            var configFiles = new[] { "appsettings.Development.json", "appsettings.json" };
            foreach (var configFile in configFiles)
            {
                var path = Path.Combine(workingDirectory, configFile);
                if (!File.Exists(path)) continue;

                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("ConnectionStrings", out var csSection)
                        && csSection.ValueKind == JsonValueKind.Object)
                    {
                        // Try "DefaultConnection" first.
                        if (csSection.TryGetProperty("DefaultConnection", out var dc)
                            && dc.ValueKind == JsonValueKind.String)
                        {
                            var v = dc.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }

                        // Fall back to the first non-empty connection string.
                        foreach (var prop in csSection.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var v = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(v)) return v;
                            }
                        }
                    }
                }
                catch
                {
                    // Silently skip malformed configs.
                }
            }

            return null;
        }

        /// <summary>
        /// Masks a connection string for logging (replaces password values with ***).
        /// </summary>
        public static string MaskConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return "(null)";

            var masked = connectionString;
            var lower = masked.ToLowerInvariant();

            // Mask Password
            if (lower.Contains("password=") || lower.Contains("pwd="))
            {
                var pattern = lower.Contains("password=") ? "password=" : "pwd=";
                var idx = masked.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = masked.Substring(idx + pattern.Length);
                    var end = after.IndexOf(';');
                    if (end > 0)
                        masked = masked.Substring(0, idx + pattern.Length) + "***" + after.Substring(end);
                    else
                        masked = masked.Substring(0, idx + pattern.Length) + "***";
                }
            }

            // Mask Secret
            if (lower.Contains("secret="))
            {
                var idx = masked.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = masked.Substring(idx + 7);
                    var end = after.IndexOf(';');
                    if (end > 0)
                        masked = masked.Substring(0, idx + 7) + "***" + after.Substring(end);
                    else
                        masked = masked.Substring(0, idx + 7) + "***";
                }
            }

            return masked;
        }
    }
}
