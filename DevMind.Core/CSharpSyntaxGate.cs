// File: CSharpSyntaxGate.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-04: refuse a .cs write that breaks the file's syntax.
//
// job-1652 inserted a block meant to go BETWEEN two test methods inside one of them, leaving it
// unterminated; untangling it took ~6 iterations. job-2143 (H-67) wrote ")]]" into two
// [LoggerMessage] attributes. Both were visible to a parser the moment the text was computed.
//
// Before a .cs file is written (patch_file, create_file, append_file — every agent write path in
// both hosts, and PatchEngine.ApplyPatch, which MCP patch_file shares), the current and the new
// text are both PARSED — Microsoft.CodeAnalysis.CSharp, syntax only: no compilation, no
// references, no semantic errors. The write is refused only when the new text has a syntax error
// the current text does not: errors are compared as a multiset of (id, message), so a file that is
// already mid-edit with a broken region can still be patched elsewhere. A new file is compared
// against no errors. Not LSP: its diagnostics have been seen stale after patches.
//
// .cshtml / .razor are out of scope (a different parser).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DevMind
{
    /// <summary>A .cs write refused because it would introduce a syntax error.</summary>
    public sealed class CSharpSyntaxGateException : InvalidOperationException
    {
        public CSharpSyntaxGateException(string message) : base(message) { }
    }

    public static class CSharpSyntaxGate
    {
        /// <summary>Starts every refusal message — lets a caller tell this refusal from other write failures.</summary>
        public const string Marker = "[SYNTAX-GATE]";

        private static readonly CSharpParseOptions Options =
            new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None);

        /// <summary>Whether the gate applies to this path (.cs only).</summary>
        public static bool Applies(string path)
            => string.Equals(Path.GetExtension(path ?? ""), ".cs", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Null when <paramref name="newText"/> may be written to <paramref name="path"/>; otherwise
        /// the refusal message. <paramref name="currentText"/> is the file as it is now, or null for
        /// a new file. Never throws: a parser failure lets the write through.
        /// </summary>
        public static string Check(string path, string currentText, string newText)
        {
            if (!Applies(path) || newText == null) return null;
            try
            {
                List<Diagnostic> after = Errors(newText);
                if (after.Count == 0) return null;

                // Multiset of the errors the file already had: each one "pays for" one identical
                // error in the new text. What is left over is new.
                var before = new Dictionary<(string, string), int>();
                if (!string.IsNullOrEmpty(currentText))
                    foreach (Diagnostic d in Errors(currentText))
                    {
                        var key = Key(d);
                        before[key] = before.TryGetValue(key, out int n) ? n + 1 : 1;
                    }

                foreach (Diagnostic d in after)
                {
                    var key = Key(d);
                    if (before.TryGetValue(key, out int n) && n > 0)
                    {
                        before[key] = n - 1;
                        continue;
                    }
                    return Describe(path, newText, d);
                }
                return null;
            }
            catch
            {
                return null;   // the gate must never be the reason a write fails for no reason
            }
        }

        /// <summary>As <see cref="Check"/>, but throws <see cref="CSharpSyntaxGateException"/> on a refusal.</summary>
        public static void Enforce(string path, string currentText, string newText)
        {
            string refusal = Check(path, currentText, newText);
            if (refusal != null) throw new CSharpSyntaxGateException(refusal);
        }

        private static List<Diagnostic> Errors(string text)
            => CSharpSyntaxTree.ParseText(text, Options)
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .OrderBy(d => d.Location.SourceSpan.Start)
                .ToList();

        private static (string, string) Key(Diagnostic d)
            => (d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));

        private static string Describe(string path, string newText, Diagnostic d)
        {
            var pos = d.Location.GetLineSpan().StartLinePosition;
            int line = pos.Line + 1, col = pos.Character + 1;

            string[] lines = newText.Replace("\r\n", "\n").Split('\n');
            var context = new StringBuilder();
            for (int ln = Math.Max(1, line - 1); ln <= Math.Min(lines.Length, line + 1); ln++)
                context.Append(ln == line ? " >> " : "    ").Append(ln.ToString().PadLeft(5)).Append(": ")
                       .Append(lines[ln - 1]).Append('\n');

            return $"{Marker} {Path.GetFileName(path)}: the proposed text has a new syntax error " +
                   $"{d.Id} at line {line}:{col}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                   context +
                   "The file was NOT written — the edit broke the file's structure; re-read the region and patch again.";
        }
    }
}
