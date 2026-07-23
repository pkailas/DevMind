// File: PdfTextReaderTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Pure-function tests for PdfTextReader.RejoinWrappedIdentifiers.
// No PDF/SQL/embedding infra needed — just string manipulation.

using Xunit;

namespace DevMind.Core.Tests
{
    public class PdfTextReaderTests
    {
        // ── Positive: wrapped identifiers rejoined ────────────────────────────

        [Fact]
        public void RejoinWrappedIdentifiers_TwoLineWrap_RejoinsAndPreservesNextLine()
        {
            string input = "PARENT_CATEGO\r\nRY_ID\r\nBinary(16) 16 The parent";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("PARENT_CATEGORY_ID", output);
            Assert.DoesNotContain("PARENT_CATEGO RY_ID", output);
            Assert.Contains("Binary(16) 16 The parent", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_TrailingSpaceBeforeWrap_Rejoins()
        {
            string input = "LAST_MODIFI \r\nED_RESOURCE";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("LAST_MODIFIED_RESOURCE", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_SimpleTwoLineWrap_Rejoins()
        {
            string input = "MANAGERIAL_LEV\r\nEL";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("MANAGERIAL_LEVEL", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_TwoLineWrapWithTrailingSpace_Rejoins()
        {
            string input = "CHARGE_FIX\r\nED_RATE";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("CHARGE_FIXED_RATE", output);
        }

        // ── Negative: bulleted items must NOT merge ───────────────────────────

        [Fact]
        public void RejoinWrappedIdentifiers_BulletedItems_NoMerge()
        {
            string input = "• CATEGORY\r\n• AW_RESOURCE\r\n• BUSINESS_PROCESS";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("AW_RESOURCE", output);
            Assert.Contains("BUSINESS_PROCESS", output);
            Assert.DoesNotContain("CATEGORYAW_RESOURCE", output);
            Assert.DoesNotContain("AW_RESOURCEBUSINESS_PROCESS", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_EnumValues_NoMerge()
        {
            string input = "• Internal User = 0\r\n• External User = 32";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Equal("• Internal User = 0\n• External User = 32", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_LineWithSpaces_NoMerge()
        {
            string input = "SECURITY_LEVEL Smallint 2 The resource";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Equal("SECURITY_LEVEL Smallint 2 The resource", output);
        }

        // ── Edge cases ────────────────────────────────────────────────────────

        [Fact]
        public void RejoinWrappedIdentifiers_EmptyInput_ReturnsEmpty()
        {
            Assert.Equal("", PdfTextReader.RejoinWrappedIdentifiers(""));
        }

        [Fact]
        public void RejoinWrappedIdentifiers_SingleBareLine_PassesThrough()
        {
            // A single bare identifier fragment is NOT merged (needs 2+ consecutive)
            string input = "CATEGORY_ID";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Equal("CATEGORY_ID", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_BlankLineBreaksRun()
        {
            // Blank line between two bare fragments breaks the run
            string input = "FOO\r\n\r\nBAR";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.DoesNotContain("FOOBAR", output);
            Assert.Contains("FOO", output);
            Assert.Contains("BAR", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_NonBareLineBreaksRun()
        {
            // A non-bare line between two bare fragments breaks the run
            string input = "FOO\r\nsome description\r\nBAR";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.DoesNotContain("FOOBAR", output);
            Assert.Contains("FOO", output);
            Assert.Contains("some description", output);
            Assert.Contains("BAR", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_NumbersOnly_NoMerge()
        {
            // Lines with only digits (no A-Z) are not bare identifier fragments
            string input = "123\r\n456";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Equal("123\n456", output);
        }
    }
}
