// File: LearnToolsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Unit tests for LearnTools SSE-frame/JSON unwrap helpers.
// No network calls — all tests use canned response strings.

using System.Text.Json;
using Xunit;

namespace DevMind.Core.Tests
{
    public class LearnToolsTests
    {
        // ── ParseSseResponse happy path ────────────────────────────────────────

        [Fact]
        public void ParseSseResponse_HappyPath_ReturnsUnwrappedPayload()
        {
            // Build the inner payload as a C# object and serialize it.
            string innerJson = JsonSerializer.Serialize(new
            {
                results = new[]
                {
                    new { title = "Test Title", url = "https://learn.microsoft.com/test", content = "Test content here" }
                }
            });

            // Build the JSON-RPC envelope, embedding innerJson as the text value
            // (JsonSerializer will escape it correctly).
            string envelope = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new[]
                    {
                        new { type = "text", text = innerJson }
                    }
                }
            });

            string sseBody = "event: message\n" + "data: " + envelope + "\n";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.NotNull(result);
            Assert.Contains("\"results\"", result);
            Assert.Contains("\"title\"", result);
            Assert.Contains("Test Title", result);
            Assert.Contains("https://learn.microsoft.com/test", result);
        }

        [Fact]
        public void ParseSseResponse_PlanTextDataLine_ReturnsPlainText()
        {
            // When the text field is not valid JSON, it should be returned as-is.
            string sseBody = @"event: message
data: {""jsonrpc"":""2.0"",""id"":1,""result"":{""content"":[{""text"":""This is plain markdown content""}]}}

";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.NotNull(result);
            Assert.Equal("This is plain markdown content", result);
        }

        // ── ParseSseResponse error paths ───────────────────────────────────────

        [Fact]
        public void ParseSseResponse_NoDataLine_ReturnsNull()
        {
            string sseBody = @"event: ping
event: heartbeat
";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.Null(result);
        }

        [Fact]
        public void ParseSseResponse_MalformedJson_ThrowsInvalidOperationException()
        {
            string sseBody = @"event: message
data: {not valid json at all

";

            Assert.Throws<InvalidOperationException>(() => LearnTools.ParseSseResponse(sseBody));
        }

        [Fact]
        public void ParseSseResponse_JsonRpcError_ThrowsInvalidOperationException()
        {
            string sseBody = @"event: message
data: {""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32602,""message"":""Invalid params""}}

";

            var ex = Assert.Throws<InvalidOperationException>(() => LearnTools.ParseSseResponse(sseBody));
            Assert.Contains("Learn MCP error", ex.Message);
            Assert.Contains("Invalid params", ex.Message);
        }

        [Fact]
        public void ParseSseResponse_MissingResult_ReturnsNull()
        {
            string sseBody = @"event: message
data: {""jsonrpc"":""2.0"",""id"":1}

";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.Null(result);
        }

        [Fact]
        public void ParseSseResponse_EmptyContentArray_ReturnsNull()
        {
            string sseBody = @"event: message
data: {""jsonrpc"":""2.0"",""id"":1,""result"":{""content"":[]}}

";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.Null(result);
        }

        [Fact]
        public void ParseSseResponse_MissingTextField_ReturnsNull()
        {
            string sseBody = @"event: message
data: {""jsonrpc"":""2.0"",""id"":1,""result"":{""content"":[{""type"":""text""}]}}

";

            string result = LearnTools.ParseSseResponse(sseBody);

            Assert.Null(result);
        }

        // ── ParseSearchResults happy path ──────────────────────────────────────

        [Fact]
        public void ParseSearchResults_HappyPath_ReturnsResults()
        {
            string payload = @"{""results"":[
                {""title"":""Result 1"",""url"":""https://learn.microsoft.com/1"",""content"":""Content 1""},
                {""title"":""Result 2"",""url"":""https://learn.microsoft.com/2"",""content"":""Content 2""}
            ]}";

            var results = LearnTools.ParseSearchResults(payload);

            Assert.NotNull(results);
            Assert.Equal(2, results.Count);
            Assert.Equal("Result 1", results[0].Title);
            Assert.Equal("https://learn.microsoft.com/1", results[0].Url);
            Assert.Equal("Content 1", results[0].Content);
            Assert.Equal("Result 2", results[1].Title);
        }

        // ── ParseSearchResults error paths ─────────────────────────────────────

        [Fact]
        public void ParseSearchResults_MissingResultsArray_ReturnsNull()
        {
            string payload = @"{""other"":""field""}";

            var results = LearnTools.ParseSearchResults(payload);

            Assert.Null(results);
        }

        [Fact]
        public void ParseSearchResults_MalformedJson_ReturnsNull()
        {
            string payload = @"not json at all";

            var results = LearnTools.ParseSearchResults(payload);

            Assert.Null(results);
        }

        [Fact]
        public void ParseSearchResults_EmptyResultsArray_ReturnsEmptyList()
        {
            string payload = @"{""results"":[]}";

            var results = LearnTools.ParseSearchResults(payload);

            Assert.NotNull(results);
            Assert.Empty(results);
        }

        [Fact]
        public void ParseSearchResults_MissingFields_ReturnsEmptyStrings()
        {
            string payload = @"{""results"":[{}]}";

            var results = LearnTools.ParseSearchResults(payload);

            Assert.NotNull(results);
            Assert.Single(results);
            Assert.Equal("", results[0].Title);
            Assert.Equal("", results[0].Url);
            Assert.Equal("", results[0].Content);
        }
    }
}
