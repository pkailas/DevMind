// File: SessionIdAdoptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The guard that keeps a resumed session from becoming two sessions.
//
// Adopting an id is only safe before anything has asked for one. The history writer keys
// every row on what Get() handed it and the nearline cache names its spill directory after
// it, so an adopt that lands after a consumer has cached the computed id splits one session
// across two — half the turns under the old id, half under the resumed one, and no error
// anywhere. That is the exact fork --resume exists to remove, so Adopt refuses rather than
// papering over a call-order mistake at startup.
//
// The startup ordering itself cannot be tested — Main needs a terminal — so this is the
// guard that stands in for it. If the adopt is ever moved below the first Get(), the guard
// throws at launch instead of silently forking.

using System;
using Xunit;

namespace DevMind.Core.Tests
{
    public class SessionIdAdoptTests
    {
        // SessionId is process-wide static state; every test here leaves it reset so it
        // cannot pin an id for whatever runs next.
        private static void WithCleanSessionId(Action body)
        {
            SessionId.Reset();
            try { body(); }
            finally { SessionId.Reset(); }
        }

        [Fact]
        public void AdoptBeforeGet_IsWhatGetReturns()
        {
            WithCleanSessionId(() =>
            {
                SessionId.Adopt("2026-09-20T101500Z-pid4242");

                Assert.Equal("2026-09-20T101500Z-pid4242", SessionId.Get());
            });
        }

        [Fact]
        public void AdoptAfterGet_Throws()
        {
            WithCleanSessionId(() =>
            {
                string computed = SessionId.Get();

                var ex = Assert.Throws<InvalidOperationException>(
                    () => SessionId.Adopt("2026-09-20T101500Z-pid4242"));

                Assert.Contains("already been resolved", ex.Message, StringComparison.Ordinal);
                Assert.Equal(computed, SessionId.Get());   // and the id it already handed out stands
            });
        }

        [Fact]
        public void AdoptTwice_Throws()
        {
            // The second call is the same mistake as adopting after Get: something already
            // holds the first id.
            WithCleanSessionId(() =>
            {
                SessionId.Adopt("first");

                Assert.Throws<InvalidOperationException>(() => SessionId.Adopt("second"));
                Assert.Equal("first", SessionId.Get());
            });
        }

        [Fact]
        public void ResetThenAdopt_Works()
        {
            WithCleanSessionId(() =>
            {
                SessionId.Get();
                SessionId.Reset();

                SessionId.Adopt("adopted-after-reset");

                Assert.Equal("adopted-after-reset", SessionId.Get());
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AdoptingNothing_Throws(string? id)
        {
            WithCleanSessionId(() =>
            {
                Assert.Throws<ArgumentException>(() => SessionId.Adopt(id));
            });
        }

        [Fact]
        public void AFreshIdIsStillComputedWhenNothingIsAdopted()
        {
            WithCleanSessionId(() =>
            {
                Assert.False(string.IsNullOrWhiteSpace(SessionId.Get()));
            });
        }
    }
}
