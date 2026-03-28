# DevMind Project Context

## Project
- **Product**: VLink Desktop
- **Company**: Verbella CMG LLC
- **Platform**: .NET Framework 4.8, VB.NET, Windows Forms
- **Solution**: Verbella.VLink.Desktop

## Context
This is a .NET Framework 4.8 VB.NET WinForms application. Judge patterns against Framework 4.8 conventions, not modern .NET. P/Invoke, shared fields, and synchronous patterns are expected. Focus on actual bugs, resource leaks, and logic errors — not modernization suggestions.

## Task: Full Project Code Review
Review ALL .vb files in this project. For each file:
1. READ the file
2. GREP for common issues (empty Catch, generic exceptions, undisposed resources)
3. Document findings with file name, line number, severity, and description

After reviewing all files, write the complete report to:
FILE: CodeReview.txt

Format the report with sections per file, severity categories (CRITICAL/WARNING/INFO), and a summary table at the end.

After writing the report, run DIFF on any modified files to verify no accidental changes were made.

Use SCRATCHPAD to track which files have been reviewed and which remain.