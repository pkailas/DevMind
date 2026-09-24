// File: WriteEcho.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// What a write says about itself in the transcript.
//
// Two things were hidden, and the first live run turned both up in the same minute.
//
// The transcript echoed the bare file name, so a write to an absolute path the model had
// invented read as "✓ Write numbers.py" — identical to a write into the folder the operator
// was watching. The folder stayed empty and the line said it had worked. The TUI is the
// trusting skin and is not going to refuse the write (headless and MCP have the sandbox),
// but trusting is not the same as silent: a write outside the working directory now says so,
// and says where it went.
//
// The second was a raw "[two-way fallback]" badge riding on the end of the line. It survived
// the vocabulary translation because the translator turns tags, not the text after them, so
// an internal phrase for "there was no base cache entry, this was overwrite detection only"
// was shown to the operator verbatim. It means the merge could not be verified, so it is a
// warning that says that.

using System;
using System.IO;

namespace DevMind
{
    /// <summary>The detail and colour for a write's transcript line. Pure.</summary>
    internal static class WriteEcho
    {
        /// <summary>What a line says when the path is not under the working directory.</summary>
        public const string OutsideNote = "(outside working directory)";

        /// <summary>What a line says when the three-way merge had no base to work from.</summary>
        public const string FuzzyNote = "(fuzzy merge — verify)";

        /// <summary>
        /// Describe a completed write.
        /// </summary>
        /// <param name="fileNameOnly">The short label used when the write landed where expected.</param>
        /// <param name="fullPath">The resolved path actually written.</param>
        /// <param name="workingDirectory">The session's working directory.</param>
        /// <param name="usedFallback">The merge fell back to overwrite detection.</param>
        /// <param name="sizeNote">Optional trailing detail such as "(16 lines)".</param>
        /// <returns>
        /// The text after the tag, and the colour — which the vocabulary (brief 12) turns into
        /// the glyph, so a warning here is a ⚠ on screen without this knowing what a glyph is.
        /// </returns>
        public static (string Detail, OutputColor Color) Describe(
            string fileNameOnly, string fullPath, string workingDirectory,
            bool usedFallback, string sizeNote = null)
        {
            bool outside = IsOutside(fullPath, workingDirectory);

            // The full path is shown precisely when the short name would mislead. Inside the
            // working directory the name is unambiguous and the path is noise; outside, the
            // name is the one thing that makes a wrong write look like a right one.
            string label = outside && !string.IsNullOrEmpty(fullPath) ? fullPath : fileNameOnly;

            string detail = label ?? string.Empty;
            if (!string.IsNullOrEmpty(sizeNote)) detail += " " + sizeNote;
            if (outside) detail += " " + OutsideNote;
            if (usedFallback) detail += " " + FuzzyNote;

            OutputColor color = outside || usedFallback ? OutputColor.Warning : OutputColor.Success;
            return (detail, color);
        }

        /// <summary>
        /// Whether a resolved path falls outside the working directory.
        /// <para>
        /// Unknown answers false. This drives a label, not a gate: a path that cannot be
        /// compared is not evidence of anything, and crying "outside" over an unresolvable
        /// path would teach the operator to ignore the word.
        /// </para>
        /// </summary>
        public static bool IsOutside(string fullPath, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;
            if (string.IsNullOrWhiteSpace(workingDirectory)) return false;

            try
            {
                string root = Path.GetFullPath(workingDirectory)
                                  .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.GetFullPath(fullPath);

                if (string.Equals(target, root, PathComparison)) return false;

                // The separator matters: without it "C:\workshop" counts as inside "C:\work".
                string prefix = root + Path.DirectorySeparatorChar;
                return !target.StartsWith(prefix, PathComparison);
            }
            catch
            {
                // A malformed path is not a finding.
                return false;
            }
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
