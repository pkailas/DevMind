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

        // ── Positive: PK-marked wrapped identifiers (PUA key glyph U+E0DA) ────

        [Fact]
        public void RejoinWrappedIdentifiers_PkMarkedWrap_RejoinsAndPreservesNextLine()
        {
            // U+E0DA is a Private-Use-Area "key" glyph used as a PK column marker
            string input = "\uE0DA EMBEDDED_PRO \r\nCESS_COUNT\r\nSmallint 2 The";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("EMBEDDED_PROCESS_COUNT", output);
            Assert.DoesNotContain("EMBEDDED_PRO CESS_COUNT", output);
            Assert.Contains("Smallint 2 The", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_PkMarkedWrap_AssignedResourceId_Rejoins()
        {
            string input = "\uE0DA ASSIGNED_RES \r\nOURCE_ID\r\nBinary(16) 16";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("ASSIGNED_RESOURCE_ID", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_PkMarkedWrap_FieldPosition_Rejoins()
        {
            string input = "\uE0DA FIELD_POSITI \r\nON\r\nSmallint 2";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("FIELD_POSITION", output);
        }

        [Fact]
        public void RejoinWrappedIdentifiers_PkMarkedWrap_GlyphPreserved()
        {
            // The joined token must still carry the U+E0DA glyph prefix
            string input = "\uE0DA EMBEDDED_PRO \r\nCESS_COUNT\r\nSmallint 2 The";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            // Marker prefix is re-attached with no separator between prefix and core
            Assert.Contains("\uE0DA EMBEDDED_PROCESS_COUNT", output);
        }

        // ── Negative: bulleted items must still NOT merge (PUA fix does not weaken) ──

        [Fact]
        public void RejoinWrappedIdentifiers_BulletedItems_StillNoMerge()
        {
            // '•' (U+2022) is NOT in the PUA range, so cores remain unchanged
            string input = "• CATEGORY\r\n• AW_RESOURCE\r\n• BUSINESS_PROCESS";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("AW_RESOURCE", output);
            Assert.Contains("BUSINESS_PROCESS", output);
            Assert.DoesNotContain("CATEGORYAW_RESOURCE", output);
            Assert.DoesNotContain("AW_RESOURCEBUSINESS_PROCESS", output);
        }

        // ── Negative: composite PK rows (complete names with datatype) must NOT merge ──

        [Fact]
        public void RejoinWrappedIdentifiers_CompositePkRows_NoMerge()
        {
            // Each line's core contains spaces (datatype info), so no merge occurs
            string input = "\uE0DA JOB_ID Binary(16) 16 x\r\n\uE0DA RESOURCE_ID Binary(16) 16 y";
            string output = PdfTextReader.RejoinWrappedIdentifiers(input);

            Assert.Contains("JOB_ID", output);
            Assert.Contains("RESOURCE_ID", output);
            Assert.DoesNotContain("JOB_IDRESOURCE_ID", output);
        }
    }
}
