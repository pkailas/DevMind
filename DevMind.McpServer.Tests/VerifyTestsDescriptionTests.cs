// File: VerifyTestsDescriptionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// verify_tests promised "harness-measured test counts (baseline_total, total, delta)".
// Measured across 58 verified runs, delta was null in 45 (78%) — 38 because the caller passed
// test_baseline "off", which makes a before->after delta impossible by definition, and the
// rest because the suite printed no parseable summary line. The description now says what
// the delta actually requires.
//
// This pins wording, and is described as such: it guards the specific sentences chosen on
// both tool sites, not the truth of the claim. The truth is pinned elsewhere — the job
// runner's baseline gate (AgentJobManager: VerifyTests && RunTestBaseline) is what makes
// the sentence accurate.

using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class VerifyTestsDescriptionTests
    {
        private static string VerifyTestsDescriptionOf(string method)
        {
            var m = typeof(AgentTaskTools).GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(m != null, $"AgentTaskTools.{method} not found");
            var p = m!.GetParameters().FirstOrDefault(x => x.Name == "verify_tests");
            Assert.True(p != null, $"{method} has no verify_tests parameter");
            var d = p!.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(d != null, $"{method}.verify_tests has no [Description]");
            return d!.Description;
        }

        [Theory]
        [InlineData("TaskStart")]
        [InlineData("TaskContinue")]
        public void VerifyTests_SaysWhatTheDeltaRequires(string method)
        {
            string d = VerifyTestsDescriptionOf(method);

            // The old sentence, which read as an unconditional promise, is gone.
            Assert.DoesNotContain("carries harness-measured test counts (baseline_total, total, delta)", d, StringComparison.Ordinal);

            // What survives: the after-run is always reported.
            Assert.Contains("harness-measured total", d, StringComparison.Ordinal);

            // What was added: the delta is conditional on the baseline mode, and "off" nulls it.
            Assert.Contains("test_baseline", d, StringComparison.Ordinal);
            Assert.Contains("before-run", d, StringComparison.Ordinal);
            Assert.Contains("null by definition", d, StringComparison.Ordinal);
        }
    }
}
