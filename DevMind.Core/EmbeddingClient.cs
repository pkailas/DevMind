// File: EmbeddingClient.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Client for an OpenAI-compatible /v1/embeddings endpoint (llama-server with
// --embeddings, e.g. Qwen3-Embedding-8B). Embeddings are truncated to
// LibraryStore's VECTOR dimensionality and L2-renormalized: Qwen3 embedding
// models are Matryoshka-trained, so a truncated prefix remains a valid
// (slightly lower-fidelity) embedding — and SQL Server 2025's VECTOR type
// caps at 1998 dimensions, below the model's native 4096.

using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>Client for an OpenAI-compatible embeddings endpoint.</summary>
    public sealed class EmbeddingClient : IDisposable
    {
        /// <summary>Stored embedding dimensionality (see class remarks).</summary>
        public const int Dimensions = 1024;

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public EmbeddingClient(string endpointUrl)
        {
            _baseUrl = LlmClient.NormalizeEndpointUrl(endpointUrl);
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        }

        /// <summary>
        /// Embeds one text and returns a <see cref="Dimensions"/>-dim L2-normalized vector.
        /// Throws HttpRequestException (with the server's error body) on failure — callers
        /// surface "is the embedding server running?" guidance.
        /// </summary>
        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            var body = new JObject
            {
                ["model"] = "embedding",
                ["input"] = text,
            };
            using (var response = await _http.PostAsync(
                _baseUrl + "/embeddings",
                new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
                ct).ConfigureAwait(false))
            {
                string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Embedding request failed: {(int)response.StatusCode} — {responseBody}");

                var json = JObject.Parse(responseBody);
                var arr = (JArray)json["data"][0]["embedding"];
                var full = new float[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    full[i] = (float)arr[i];
                return TruncateAndNormalize(full, Dimensions);
            }
        }

        /// <summary>
        /// Truncates to <paramref name="dims"/> and L2-renormalizes (unit length). MRL
        /// truncation requires renormalization for cosine distance to stay meaningful.
        /// </summary>
        internal static float[] TruncateAndNormalize(float[] full, int dims)
        {
            var v = new float[dims];
            Array.Copy(full, v, Math.Min(dims, full.Length));
            double sumSq = 0;
            foreach (float f in v) sumSq += (double)f * f;
            double norm = Math.Sqrt(sumSq);
            if (norm > 0)
            {
                for (int i = 0; i < v.Length; i++)
                    v[i] = (float)(v[i] / norm);
            }
            return v;
        }

        /// <summary>Serializes a vector as the JSON array literal SQL Server's
        /// CAST(@p AS VECTOR(n)) accepts.</summary>
        public static string ToJsonArray(float[] v)
        {
            var sb = new StringBuilder(v.Length * 12);
            sb.Append('[');
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v[i].ToString("G9", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public void Dispose() => _http.Dispose();
    }
}
