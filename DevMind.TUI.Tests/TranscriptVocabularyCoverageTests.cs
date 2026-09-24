// File: TranscriptVocabularyCoverageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// A tag added tomorrow has to be given a word.
//
// The translation table degrades gracefully by design: an unmapped tag still appears, as
// "● Some Tag". That is the right behaviour at runtime and the wrong thing to discover in a
// screenshot — it reads as a defect in the feature rather than as a gap in a table, and
// nothing about it is loud enough to notice while it scrolls past.
//
// So the table is checked against the source that feeds it, the same argument
// ClaudeMdSlashCommandParityTests makes for the command list: derive the expected set
// structurally rather than trusting one somebody typed. Adding a tag to an emitting file now
// forces a choice — map it, or say here that it never reaches the transcript.

using System.Text.RegularExpressions;
using Xunit;

namespace DevMind.TUI.Tests
{
    public class TranscriptVocabularyCoverageTests
    {
        // The files whose AppendOutput calls reach the TUI transcript. BufferedAgenticHost is
        // deliberately absent: it is the headless host, its tags go to a buffer the CLI and
        // transcript_tail read raw, and they never pass through the translator.
        private static readonly string[] EmittingFiles =
        {
            @"DevMind.Core\LoopDriver.cs",
            @"DevMind.Core\AgenticExecutor.cs",
            @"DevMind.TUI\TuiAgenticHost.cs",
            @"DevMind.TUI\Program.cs",
            @"DevMind.TUI\TuiLoopCallbacks.cs",
        };

        // Tags that exist in those files but never reach the transcript, each for a stated
        // reason. A tag belongs here only when it cannot be seen by a user.
        private static readonly HashSet<string> NeverReachesTheTranscript = new(StringComparer.Ordinal)
        {
            // Diagnostics: written to the trace file or DevMindLog, not to the output view.
            "DIAG", "FLUSH", "TRIM", "CLS", "LIVETAIL", "INIT", "PASTE", "REBUILD",

            // The quiet filter drops these before the translator ever sees them (brief 09):
            // their numbers live in the status bar.
            "LLM", "TOOL_USE",
        };

        private static string RepoRoot()
        {
            // DevMind.TUI.Tests/bin/<cfg>/<tfm>/ → up 4 = repo root.
            string dir = AppContext.BaseDirectory;
            for (int up = 0; up < 4; up++)
            {
                var d = new DirectoryInfo(dir);
                if (d.Parent == null) break;
                dir = d.Parent.FullName;
            }
            return dir;
        }

        [Fact]
        public void EveryTagAnEmittingFileWrites_IsEitherTranslatedOrDeclaredUnreachable()
        {
            string root = RepoRoot();
            var unmapped = new SortedSet<string>(StringComparer.Ordinal);
            int filesRead = 0;

            foreach (string relative in EmittingFiles)
            {
                string path = Path.Combine(root, relative);
                Assert.True(File.Exists(path), $"Emitting file not found: {path} — RepoRoot() resolved wrong.");
                filesRead++;

                foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\[([A-Z][A-Z0-9 _-]*)\]"))
                {
                    string tag = m.Groups[1].Value;
                    if (NeverReachesTheTranscript.Contains(tag)) continue;
                    if (TranscriptVocabulary.Knows(tag)) continue;
                    unmapped.Add(tag);
                }
            }

            Assert.Equal(EmittingFiles.Length, filesRead);
            Assert.True(unmapped.Count == 0,
                "These tags reach the TUI transcript with no word to say them in: "
                + string.Join(", ", unmapped)
                + ". Add each to TranscriptVocabulary's table, or to NeverReachesTheTranscript "
                + "with the reason it cannot be seen.");
        }

        [Fact]
        public void TheDeclaredUnreachableListIsNotAWayToSkipTheTable()
        {
            // A tag cannot be both translated and declared unreachable; that would mean one of
            // the two statements is stale and nobody would know which.
            foreach (string tag in NeverReachesTheTranscript)
                Assert.False(TranscriptVocabulary.Knows(tag),
                    $"\"{tag}\" is both translated and declared unreachable — one of those is wrong.");
        }
    }
}
