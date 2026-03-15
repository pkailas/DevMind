// File: ResponseParser.cs  v5.0.6
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    public enum BlockType { Text, File, Patch, Shell, ReadRequest, Scratchpad }

    public class ResponseBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; }   // raw text, file source, or full PATCH text
        public string FileName { get; set; }  // for File, Patch, ReadRequest
        public string Command { get; set; }   // for Shell
        public int  RangeStart    { get; set; }  // for ReadRequest line-range (0 = full read)
        public int  RangeEnd      { get; set; }  // for ReadRequest line-range (0 = full read)
        public bool ForceFullRead { get; set; }  // READ! — bypass outline-first behavior
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
        // Matches READ filename, READ filename:start-end, or READ filename:line (only used when no PATCH/FILE present)
        private static readonly Regex _readLine       = new Regex(@"^READ\s+(\S+\.\S+?)(?::(\d+)(?:-(\d+))?)?\s*$",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches READ! filename — force full content regardless of file size
        private static readonly Regex _forceReadLine  = new Regex(@"^READ!\s+(\S+\.\S+?)\s*$",                        RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches SCRATCHPAD: block start (must be on its own line)
        private static readonly Regex _scratchpadStart = new Regex(@"^SCRATCHPAD:\s*$",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches END_SCRATCHPAD terminator used to close a SCRATCHPAD block
        private static readonly Regex _scratchpadEnd   = new Regex(@"^\s*END_SCRATCHPAD\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Markdown fence: ```lang or ```
        private static readonly Regex _mdFence         = new Regex(@"^```",              RegexOptions.Compiled);

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

                // READ! line — force full content (checked before plain READ)
                Match forceReadMatch = _forceReadLine.Match(line);
                if (forceReadMatch.Success && !hasActionableBlocks)
                {
                    FlushText(blocks, textBuf);
                    blocks.Add(new ResponseBlock
                    {
                        Type          = BlockType.ReadRequest,
                        FileName      = forceReadMatch.Groups[1].Value,
                        ForceFullRead = true
                    });
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
                        int rangeStart = 0, rangeEnd = 0;
                        if (readMatch.Groups[2].Success)
                        {
                            int.TryParse(readMatch.Groups[2].Value, out rangeStart);
                            // Group 3 present → explicit end line; absent → single-line read (start == end)
                            rangeEnd = readMatch.Groups[3].Success
                                ? (int.TryParse(readMatch.Groups[3].Value, out int re) ? re : rangeStart)
                                : rangeStart;
                        }
                        blocks.Add(new ResponseBlock
                        {
                            Type       = BlockType.ReadRequest,
                            FileName   = readMatch.Groups[1].Value,
                            RangeStart = rangeStart,
                            RangeEnd   = rangeEnd
                        });
                        i++;
                        continue;
                    }
                    // else fall through to text
                }

                // SCRATCHPAD: block — always parsed regardless of actionable blocks
                if (_scratchpadStart.IsMatch(line))
                {
                    FlushText(blocks, textBuf);
                    var scratchBuf = new StringBuilder();
                    i++;
                    while (i < lines.Length && !_scratchpadEnd.IsMatch(lines[i]))
                    {
                        scratchBuf.AppendLine(lines[i]);
                        i++;
                    }
                    if (i < lines.Length && _scratchpadEnd.IsMatch(lines[i]))
                        i++; // consume the --- terminator
                    string scratchContent = scratchBuf.ToString().TrimEnd('\r', '\n', ' ');
                    if (!string.IsNullOrWhiteSpace(scratchContent))
                        blocks.Add(new ResponseBlock { Type = BlockType.Scratchpad, Content = scratchContent });
                    continue;
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
