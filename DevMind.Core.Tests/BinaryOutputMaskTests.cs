// File: BinaryOutputMaskTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Binary data in shell output is collapsed to a marker before the agent reads it (job-1694:
// several KB of raw JPEG bytes from a PDF dump landed in its context). Ordinary text —
// including non-ASCII letters, box-drawing characters and ANSI colour codes — is untouched.

using System.Text;
using Xunit;
using static DevMind.Core.Tests.LoopDriverTest;

namespace DevMind.Core.Tests
{
    public sealed class BinaryOutputMaskTests
    {
        // The start of job-1694's JPEG dump, verbatim from its transcript: an SOI/APP0 header
        // read through the OEM code page ("├┐├" is the mojibake of an 0xFF marker), then the
        // quantization table and a NUL run.
        private const string JpegHeader =
            "\u251C\u2510\u251C\u00FF\u251C\u2510\u251C\u00E1\0\u0010JFIF\0\u0001\u0001\0\0\u0001\0\u0001\0\0" +
            "\u251C\u2510\u251C\u00A2\0C\0\u0003\u0002\u0002\u0003\u0002\u0002\u0003\u0003\u0003\u0003\u0004" +
            "\u0003\u0003\u0004\u0005\b\u0005\u0005\u0004\u0004\u0005";

        private static string JpegLike(int nulRun) =>
            JpegHeader + new string('\u0014', 50) + "\u251C\u2510\u251C\u00C7\0\u0011\b\u0002X\u0003 \u0003\u0001\"\0"
            + new string('\0', nulRun);

        [Fact]
        public void JpegBytesEmbeddedInText_AreCollapsedToAMarker()
        {
            string binary = JpegLike(1000);
            string output = "Extracting image 1 of 3 from page 2...\r\n" + binary + "\r\nDone: 3 images, 41,207 bytes.\r\n";

            string masked = BinaryOutputMask.Collapse(output);

            Assert.DoesNotContain('\0', masked);   // char overload is ordinal; the string one is culture-aware and ignores NUL
            Assert.DoesNotContain("JFIF", masked);
            // The region runs from the first control char to the last: the 8 printable mojibake
            // chars in front of the first NUL stay, everything after them becomes ONE marker
            // sized to what it replaced.
            string lead = JpegHeader.Substring(0, 8);
            string size = (binary.Length - lead.Length).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(
                "Extracting image 1 of 3 from page 2...\r\n" + lead +
                $"[... {size} bytes of binary data ...]" +
                "\r\nDone: 3 images, 41,207 bytes.\r\n",
                masked);
        }

        [Fact]
        public void ReplacementCharacterRuns_FromInvalidUtf8_AreCollapsed()
        {
            byte[] bytes = new byte[400];
            new System.Random(7).NextBytes(bytes);
            string decoded = new UTF8Encoding(false).GetString(bytes);

            string masked = BinaryOutputMask.Collapse("before\n" + decoded + "\nafter");

            Assert.StartsWith("before\n", masked);
            Assert.EndsWith("\nafter", masked);
            Assert.Contains("bytes of binary data", masked);
            Assert.DoesNotContain('\uFFFD', masked);
        }

        [Theory]
        [InlineData("Build succeeded.\r\n    0 Warning(s)\r\n    0 Error(s)\r\n")]
        [InlineData("Stra\u00DFe, \u00C4rger, na\u00EFve, caf\u00E9 \u2014 \u6771\u4EAC, \u0395\u03BB\u03BB\u03B7\u03BD\u03B9\u03BA\u03AC, \u043A\u0438\u0440\u0438\u043B\u043B\u0438\u0446\u0430\n")]
        [InlineData("\u251C\u2500\u2500 DevMind.Core\n\u2502   \u251C\u2500\u2500 LoopDriver.cs\n\u2502   \u2514\u2500\u2500 LoopState.cs\n\u2514\u2500\u2500 README.md\n\u2554\u2550\u2550\u2550\u2557\n\u2551 ok \u2551\n\u255A\u2550\u2550\u2550\u255D\n")]
        [InlineData("col1\tcol2\tcol3\n1\t2\t3\n")]
        [InlineData("")]
        public void OrdinaryText_IsUntouched(string text)
        {
            Assert.Equal(text, BinaryOutputMask.Collapse(text));
        }

        [Fact]
        public void AnsiColouredTestOutput_IsUntouched()
        {
            // One ESC every few characters — lots of control chars in total, but never dense.
            var sb = new StringBuilder();
            for (int i = 0; i < 40; i++)
                sb.Append("\u001b[32m  Passed\u001b[0m Test").Append(i).Append(" [1 ms]\n");
            string text = sb.ToString();

            Assert.Equal(text, BinaryOutputMask.Collapse(text));
        }

        [Fact]
        public void AShortControlRun_BelowTheThreshold_IsKept()
        {
            string text = "a\0\0\0\0\0\0\0\0\0\0b";   // 10 NULs
            Assert.Equal(text, BinaryOutputMask.Collapse(text));
        }

        [Fact]
        public void ARunJustOverTheThreshold_IsCollapsed()
        {
            string text = "a" + new string('\u0001', 17) + "b";
            Assert.Equal("a[... 17 bytes of binary data ...]b", BinaryOutputMask.Collapse(text));
        }

        [Fact]
        public void TwoBlobsSeparatedByAText_Line_BecomeTwoMarkers()
        {
            string text = new string('\0', 20) + "\nimage 2 of 2: 640x480 JPEG\n" + new string('\0', 30);
            Assert.Equal(
                "[... 20 bytes of binary data ...]\nimage 2 of 2: 640x480 JPEG\n[... 30 bytes of binary data ...]",
                BinaryOutputMask.Collapse(text));
        }

        // ── The hook: run_shell's tool result is what the agent reads ────────────────

        [Fact]
        public async Task RunShellToolResult_HasTheBinaryCollapsed()
        {
            using var env = new Env();
            // FakeHost only returns scripted output for a non-zero exit; the exit code is
            // irrelevant to the masking.
            env.Host.ShellResults["dump.ps1"] = (exitCode: 1, output: "header\n" + JpegLike(500) + "\ntrailer\n");

            var turn = await env.RunToolTurn(Tool("run_shell", "{\"command\":\"dump.ps1\"}"));

            string shellOutput = turn.Result!.ShellOutput;
            Assert.Contains("bytes of binary data", shellOutput);
            Assert.DoesNotContain('\0', shellOutput);
            Assert.StartsWith("header\n" + JpegHeader.Substring(0, 8) + "[... ", shellOutput);
            Assert.EndsWith("\ntrailer\n", shellOutput);
        }
    }
}
