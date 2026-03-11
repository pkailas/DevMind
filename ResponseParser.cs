// File: ResponseParser.cs  v5.0.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    public enum BlockType { Text, File, Patch, Shell, ReadRequest }

    public class ResponseBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; }   // raw text, file source, or full PATCH text
        public string FileName { get; set; }  // for File, Patch, ReadRequest
        public string Command { get; set; }   // for Shell
    }

    public static class ResponseParser
    {
        // Matches FILE: filename
        private static readonly Regex _fileStart   = new Regex(@"^FILE:\s*(\S+\.\w+)",       RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches END_FILE (optional trailing whitespace)
        private static readonly Regex _fileEnd     = new Regex(@"^END_FILE\s*$",              RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches PATCH filename
        private static readonly Regex _patchStart  = new Regex(@"^PATCH\s+(\S+\.\S+)",        RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches SHELL: command
        private static readonly Regex _shellLine   = new Regex(@"^\s*SHELL:\s*(.+)",          RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches READ filename (only used when no PATCH/FILE present)
        private static readonly Regex _readLine    = new Regex(@"^READ\s+(\S+\.\S+)",         RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Markdown fence: ```lang or ```
        private static readonly Regex _mdFence     = new Regex(@"^```",                       RegexOptions.Compiled);

        public static List<ResponseBlock> Parse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return new List<ResponseBlock>();

            // Normalize line endings
            string normalized = response.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');

            // Pre-pass: strip markdown fence lines that wrap PATCH/FILE blocks
            lines = StripMarkdownFenceLines(lines);

            // Determine whether there are any PATCH or FILE blocks (for READ treatment)
            bool hasActionableBlocks = HasActionableBlocks(lines);

            var blocks = new List<ResponseBlock>();
            var textBuf = new StringBuilder();
            string lastShellCommand = null;

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];

                // FILE: block
                Match fileMatch = _fileStart.Match(line);
                if (fileMatch.Success)
                {
                    FlushText(blocks, textBuf);
                    string fileName = fileMatch.Groups[1].Value;
                    var contentBuf = new StringBuilder();
                    i++;
                    while (i < lines.Length && !_fileEnd.IsMatch(lines[i]))
                    {
                        contentBuf.AppendLine(lines[i]);
                        i++;
                    }
                    // consume END_FILE line if present
                    if (i < lines.Length && _fileEnd.IsMatch(lines[i]))
                        i++;
                    // Second-layer defense: strip any trailing END / END_FILE line that
                    // leaked into the buffer due to token boundary splits during streaming.
                    string fileBlockContent = contentBuf.ToString().TrimEnd('\r', '\n', ' ');
                    int lastNl = fileBlockContent.LastIndexOf('\n');
                    string lastLine = lastNl >= 0 ? fileBlockContent.Substring(lastNl + 1).Trim() : fileBlockContent.Trim();
                    if (lastLine == "END_FILE" || lastLine == "END" || lastLine.StartsWith("END_FILE"))
                        fileBlockContent = fileBlockContent.Substring(0, lastNl < 0 ? 0 : lastNl).TrimEnd('\r', '\n', ' ');
                    blocks.Add(new ResponseBlock
                    {
                        Type = BlockType.File,
                        FileName = fileName,
                        Content = fileBlockContent
                    });
                    continue;
                }

                // PATCH block
                Match patchMatch = _patchStart.Match(line);
                if (patchMatch.Success)
                {
                    FlushText(blocks, textBuf);
                    string fileName = patchMatch.Groups[1].Value;
                    var patchBuf = new StringBuilder();
                    patchBuf.AppendLine(line);  // include the PATCH header line
                    i++;
                    while (i < lines.Length)
                    {
                        string pl = lines[i];
                        // Terminators: FILE:, another PATCH, SHELL:, or end
                        if (_fileStart.IsMatch(pl) || _patchStart.IsMatch(pl) || _shellLine.IsMatch(pl))
                            break;
                        patchBuf.AppendLine(pl);
                        i++;
                    }
                    blocks.Add(new ResponseBlock
                    {
                        Type = BlockType.Patch,
                        FileName = fileName,
                        Content = patchBuf.ToString().TrimEnd('\r', '\n', ' ')
                    });
                    continue;
                }

                // SHELL: directive
                Match shellMatch = _shellLine.Match(line);
                if (shellMatch.Success)
                {
                    FlushText(blocks, textBuf);
                    string cmd = shellMatch.Groups[1].Value.Trim();
                    // Deduplicate consecutive identical commands
                    if (!string.Equals(cmd, lastShellCommand, StringComparison.Ordinal))
                    {
                        blocks.Add(new ResponseBlock { Type = BlockType.Shell, Command = cmd });
                        lastShellCommand = cmd;
                    }
                    i++;
                    continue;
                }

                // READ line
                Match readMatch = _readLine.Match(line);
                if (readMatch.Success)
                {
                    if (!hasActionableBlocks)
                    {
                        FlushText(blocks, textBuf);
                        blocks.Add(new ResponseBlock
                        {
                            Type = BlockType.ReadRequest,
                            FileName = readMatch.Groups[1].Value
                        });
                        i++;
                        continue;
                    }
                    // else fall through to text
                }

                // Plain text
                textBuf.AppendLine(line);
                i++;
            }

            FlushText(blocks, textBuf);
            return blocks;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void FlushText(List<ResponseBlock> blocks, StringBuilder buf)
        {
            if (buf.Length == 0) return;
            string content = buf.ToString().TrimEnd('\r', '\n');
            buf.Clear();
            if (string.IsNullOrWhiteSpace(content)) return;
            blocks.Add(new ResponseBlock { Type = BlockType.Text, Content = content });
        }

        /// <summary>
        /// Remove markdown fence lines (``` or ```lang) that immediately wrap PATCH or FILE blocks.
        /// A fence line is dropped only when it is adjacent to (or surrounds) a directive line.
        /// </summary>
        private static string[] StripMarkdownFenceLines(string[] lines)
        {
            var result = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (_mdFence.IsMatch(lines[i]))
                {
                    // Check if the next non-empty line is a directive, or the previous was
                    bool nextIsDirective = false;
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[j])) continue;
                        nextIsDirective = _patchStart.IsMatch(lines[j])
                                       || _fileStart.IsMatch(lines[j])
                                       || _shellLine.IsMatch(lines[j]);
                        break;
                    }
                    bool prevIsDirective = false;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[j])) continue;
                        prevIsDirective = _patchStart.IsMatch(lines[j])
                                       || _fileStart.IsMatch(lines[j])
                                       || _shellLine.IsMatch(lines[j])
                                       || lines[j].StartsWith("FIND:", StringComparison.OrdinalIgnoreCase)
                                       || lines[j].StartsWith("REPLACE:", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                    if (nextIsDirective || prevIsDirective)
                        continue;  // drop fence
                }
                result.Add(lines[i]);
            }
            return result.ToArray();
        }

        private static bool HasActionableBlocks(string[] lines)
        {
            foreach (string line in lines)
            {
                if (_patchStart.IsMatch(line) || _fileStart.IsMatch(line))
                    return true;
            }
            return false;
        }
    }
}
