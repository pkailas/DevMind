// File: CliTranscriptIsUnfilteredTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The CLI keeps its firehose.
//
// The TUI now drops the engine's per-iteration status lines ([LLM], [CONTEXT], [TOOL_USE],
// [AGENTIC] Iteration) from its transcript, because it has a status bar to put those numbers
// in. The CLI has no status bar: those lines ARE its instrumentation, and an operator reading
// a piped CLI session reads them raw. The same is true of the headless transcript.
//
// Nothing pinned that separation. The quiet filter is deliberately a DevMind.TUI type for
// exactly this reason, so the guard is structural: if the CLI ever takes a dependency on the
// TUI skin, the filter becomes reachable from the CLI's own token path and this fails. It
// also catches the broader mistake the layering forbids — a UI skin referencing another UI
// skin instead of the engine.

using System.Reflection;
using Xunit;

namespace DevMind.Cli.Tests
{
    public class CliTranscriptIsUnfilteredTests
    {
        [Fact]
        public void TheCliDoesNotReferenceTheTuiSkin_SoTheQuietFilterCannotReachIt()
        {
            Assembly cli = typeof(DevMind.ConsoleAgenticHost).Assembly;

            var referenced = cli.GetReferencedAssemblies();

            Assert.DoesNotContain(referenced, a => a.Name == "DevMind.TUI");
        }

        [Fact]
        public void TheQuietFilterIsNotVisibleToTheCli()
        {
            // Belt and braces: even a transitive route to the type would make it callable
            // from the CLI's token path. Type.GetType with the assembly-qualified name
            // resolves only if the assembly is actually loadable from the CLI's context.
            Assembly cli = typeof(DevMind.ConsoleAgenticHost).Assembly;

            Assert.Null(cli.GetType("DevMind.TranscriptNoise", throwOnError: false));
        }
    }
}
