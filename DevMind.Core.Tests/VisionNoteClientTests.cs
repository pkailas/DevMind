// File: VisionNoteClientTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Unit tests for VisionNoteClient reachability probing — the safety-critical path
// that decides whether /library PDF ingest routes to the dedicated vision endpoint
// or warns + falls back. Must NEVER throw (a down endpoint returns false).

using Xunit;

namespace DevMind.Core.Tests
{
    public class VisionNoteClientTests
    {
        [Fact]
        public async Task IsReachable_BlankEndpoint_ReturnsFalse()
        {
            Assert.False(await VisionNoteClient.IsReachableAsync(
                null, "", TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.False(await VisionNoteClient.IsReachableAsync(
                "", "", TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.False(await VisionNoteClient.IsReachableAsync(
                "   ", "", TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        [Fact]
        public async Task IsReachable_DeadEndpoint_ReturnsFalseWithoutThrowing()
        {
            // Nothing listens here → connection refused → must resolve to false, not throw,
            // so the caller can warn and offer the fallback.
            bool up = await VisionNoteClient.IsReachableAsync(
                "http://127.0.0.1:59997/v1", "", TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.False(up);
        }
    }
}
