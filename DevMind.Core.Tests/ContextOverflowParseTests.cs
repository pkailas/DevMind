// File: ContextOverflowParseTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The recovery path hangs entirely off this classification. Get it wrong in one direction
// and a 400 for an unknown model is "recovered" by compacting the history and re-sending
// the same doomed request; get it wrong in the other and the one failure DevMind can
// actually fix ends the job.
//
// The body in TheRealRejection is the one llama.cpp b10499 returned live on 2026-09-22 at
// n_ctx 262,144, copied verbatim rather than reconstructed — the point of these numbers is
// that they came from a tokenizer, not from an estimate.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ContextOverflowParseTests
    {
        // Captured verbatim from the server. Do not tidy: the field order, the spacing and
        // the trailing clause are what a real build sends.
        private const string TheRealRejection =
            "{\"error\":{\"code\":400," +
            "\"message\":\"request (450011 tokens) exceeds the available context size (262144 tokens), try increasing it\"," +
            "\"type\":\"exceed_context_size_error\"," +
            "\"n_prompt_tokens\":450011," +
            "\"n_ctx\":262144}}";

        [Fact]
        public void TheRealRejection_IsRecognised_WithBothNumbers()
        {
            Assert.True(ContextOverflow.TryParse(400, TheRealRejection, out var info));

            Assert.Equal(450011, info.PromptTokens);
            Assert.Equal(262144, info.ContextSize);
            Assert.Contains("exceeds the available context size", info.ServerMessage, StringComparison.Ordinal);
        }

        // Some proxies reject on size before the model server sees the request, with no body
        // to parse. The condition is still unambiguous, and the numbers are simply unknown.
        [Fact]
        public void PayloadTooLarge_IsRecognised_WithNoNumbers()
        {
            Assert.True(ContextOverflow.TryParse(413, string.Empty, out var info));

            Assert.Equal(0, info.PromptTokens);
            Assert.Equal(0, info.ContextSize);
            Assert.Equal(string.Empty, info.ServerMessage);
        }

        // The discriminating case. A 400 is the same status the overflow arrives on, so if
        // the type were ignored this would be "recovered" by compacting and re-sending a
        // request the server will reject for exactly the same reason, forever.
        [Fact]
        public void ADifferentFourHundred_IsNotOverflow()
        {
            Assert.False(ContextOverflow.TryParse(
                400,
                "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"unknown model\"}}",
                out var info));

            Assert.Null(info);
        }

        // Status wins over wording: a 500 carrying these words is the server failing during
        // generation, not refusing an oversized prompt, and retrying it is not recovery.
        [Fact]
        public void TheSameBodyOnAFiveHundred_IsNotOverflow()
        {
            Assert.False(ContextOverflow.TryParse(500, TheRealRejection, out var info));
            Assert.Null(info);
        }

        // This runs while an error is already being handled. A parser that throws here would
        // replace a diagnosable HTTP failure with a stack trace from the error path.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("<html><body>502 Bad Gateway</body></html>")]
        [InlineData("{\"error\":")]
        [InlineData("{}")]
        [InlineData("{\"error\":null}")]
        [InlineData("[1,2,3]")]
        public void AMalformedBody_IsFalse_AndNeverThrows(string body)
        {
            Assert.False(ContextOverflow.TryParse(400, body, out var info));
            Assert.Null(info);
        }

        // A build that sends the prose but not the type is still recognised — layer 2.
        [Fact]
        public void TheMessageTextAlone_IsEnough()
        {
            Assert.True(ContextOverflow.TryParse(
                400,
                "{\"error\":{\"message\":\"request (99 tokens) exceeds the available context size (50 tokens)\"}}",
                out var info));

            Assert.Equal(0, info.PromptTokens);   // this build reports no structured counts
            Assert.Equal(0, info.ContextSize);
        }

        [Fact]
        public void TheMessageTextIsMatchedCaseInsensitively()
        {
            Assert.True(ContextOverflow.TryParse(
                400,
                "{\"error\":{\"message\":\"Request EXCEEDS THE AVAILABLE CONTEXT SIZE for this model\"}}",
                out _));
        }

        // A 413 whose body happens to be unparseable is still a 413.
        [Fact]
        public void PayloadTooLarge_WithAJunkBody_IsStillRecognised()
        {
            Assert.True(ContextOverflow.TryParse(413, "<html>too large</html>", out var info));
            Assert.Equal(0, info.PromptTokens);
        }

        // Absent, null and nonsensical counts all read as 0 rather than propagating a value
        // the compaction sizer would treat as a quantity.
        [Theory]
        [InlineData("\"n_prompt_tokens\":null,\"n_ctx\":null")]
        [InlineData("\"n_prompt_tokens\":-5,\"n_ctx\":-1")]
        [InlineData("\"n_prompt_tokens\":\"lots\",\"n_ctx\":\"loads\"")]
        public void UnusableCounts_ReadAsZero(string counts)
        {
            string body = "{\"error\":{\"type\":\"exceed_context_size_error\"," +
                          "\"message\":\"too big\"," + counts + "}}";

            Assert.True(ContextOverflow.TryParse(400, body, out var info));
            Assert.Equal(0, info.PromptTokens);
            Assert.Equal(0, info.ContextSize);
        }
    }
}
