// File: ResponseParser.cs  v5.6.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    public enum BlockType { Text, File, Patch, Shell, ReadRequest, Scratchpad, Done, Grep, Find, Delete, Rename }

    public class ResponseBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; }   // raw text, file source, or full PATCH text
        public string FileName { get; set; }  // for File, Patch, ReadRequest, Grep
        public string Command { get; set; }   // for Shell
        public int  RangeStart    { get; set; }  // for ReadRequest/Grep line-range (0 = full read)
        public int  RangeEnd      { get; set; }  // for ReadRequest/Grep line-range (0 = full read)
        public bool ForceFullRead { get; set; }  // READ! — bypass outline-first behavior
        public string Pattern     { get; set; }  // for Grep/Find — substring search pattern
        public string GlobPattern { get; set; }  // for Find — glob file pattern (e.g. *.cs, Services/*.cs)
        public string RenameFrom  { get; set; }  // for Rename — source filename
        public string RenameTo    { get; set; }  // for Rename — destination filename
    }

    public static class ResponseParser
    {
        // ── Directive patterns ────────────────────────────────────────────────────

        // Matches FILE: filename
        private static readonly Regex _fileStart      = new Regex(@"^FILE:\s*(\S+\.\w+)",         RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches END_FILE (optional trailing whitespace)
        private static readonly Regex _fileEnd        = new Regex(@"^END_FILE\s*$",                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches PATCH filename
        private static readonly Regex _patchStart     = new Regex(@"^PATCH\s+(\S+\.\S+)",          RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches END_PATCH (optional trailing whitespace)
        private static readonly Regex _patchEnd       = new Regex(@"^END_PATCH\s*$",               RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches FIND: exactly (optional trailing whitespace)
        private static readonly Regex _findLine       = new Regex(@"^FIND:\s*$",                   RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches REPLACE: exactly (optional trailing whitespace)
        private static readonly Regex _replaceLine    = new Regex(@"^REPLACE:\s*$",                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches SHELL: command
        private static readonly Regex _shellLine      = new Regex(@"^\s*SHELL:\s*(.+)",            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches READ filename, READ filename:start-end, or READ filename:line
        private static readonly Regex _readLine       = new Regex(@"^READ\s+(\S+\.\S+?)(?::(\d+)(?:-(\d+))?)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches READ! filename — force full content regardless of file size
        private static readonly Regex _forceReadLine  = new Regex(@"^READ!\s+(\S+\.\S+?)\s*$",    RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches SCRATCHPAD: block start (must be on its own line)
        private static readonly Regex _scratchpadStart = new Regex(@"^SCRATCHPAD:\s*$",            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches END_SCRATCHPAD terminator
        private static readonly Regex _scratchpadEnd  = new Regex(@"^\s*END_SCRATCHPAD\s*$",       RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches a line that is exactly "DONE"
        private static readonly Regex _doneLine       = new Regex(@"^\s*DONE\s*$",                 RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches GREP: "pattern" filename or GREP: "pattern" filename:start-end
        private static readonly Regex _grepLine       = new Regex(@"^GREP:\s+""([^""]+)""\s+(\S+\.\S+?)(?::(\d+)(?:-(\d+))?)?\s*$", RegexOptions.Compiled);
        // Matches FIND: "pattern" glob or FIND: "pattern" glob:start-end  (glob may contain * and /)
        private static readonly Regex _findFileLine   = new Regex(@"^FIND:\s+""([^""]+)""\s+(\S+)(?::(\d+)(?:-(\d+))?)?\s*$", RegexOptions.Compiled);
        // Matches DELETE filename (no colon)
        private static readonly Regex _deleteLine     = new Regex(@"^DELETE\s+(\S+\.\S+)\s*$",     RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Matches RENAME OldFile.cs NewFile.cs
        private static readonly Regex _renameLine     = new Regex(@"^RENAME\s+(\S+)\s+(\S+)\s*$",  RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Markdown fence: ```lang or ```
        private static readonly Regex _mdFence        = new Regex(@"^```",                         RegexOptions.Compiled);

        // ── Parser states ─────────────────────────────────────────────────────────

        private enum State { TopLevel, InFile, InPatch, InScratchpad }

        // Sub-state inside InPatch
        private enum PatchSection { Preamble, InFind, InReplace }

        // ── Public API ────────────────────────────────────────────────────────────

        public static List<ResponseBlock> Parse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return new List<ResponseBlock>();

            // Normalize line endings
            string normalized = response.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');

            // Pre-pass: strip markdown fence lines that wrap PATCH/FILE blocks
            lines = StripMarkdownFenceLines(lines);

            // Determine whether there are any PATCH or FILE blocks (controls READ treatment)
            bool hasActionableBlocks = HasActionableBlocks(lines);

            System.Diagnostics.Debug.WriteLine($"[PARSER-DIAG] Parsing {lines.Length} lines (after fence strip), hasActionableBlocks={hasActionableBlocks}");
            for (int dbgI = 0; dbgI < Math.Min(lines.Length, 30); dbgI++)
                System.Diagnostics.Debug.WriteLine($"[PARSER-DIAG] line[{dbgI}]: \"{lines[dbgI]}\"");

            var blocks       = new List<ResponseBlock>();
            var textBuf      = new StringBuilder();   // pending TopLevel text
            var fileBuf      = new StringBuilder();   // content inside FILE: block
            var patchBuf     = new StringBuilder();   // raw PATCH text (header + all pairs)
            var scratchBuf   = new StringBuilder();   // content inside SCRATCHPAD: block
            var findBuf      = new StringBuilder();   // current FIND: section text
            var replaceBuf   = new StringBuilder();   // current REPLACE: section text

            string lastShellCommand = null;
            string fileBlockName    = null;           // filename of the current FILE: block
            string patchFileName    = null;           // filename of the current PATCH block
            PatchSection patchSec   = PatchSection.Preamble;

            State state = State.TopLevel;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                switch (state)
                {
                    // ── TopLevel ──────────────────────────────────────────────────
                    case State.TopLevel:
                        ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                        hasActionableBlocks,
                                        ref state, ref fileBlockName,
                                        ref patchFileName, patchBuf, ref patchSec,
                                        scratchBuf);
                        break;

                    // ── InFile ────────────────────────────────────────────────────
                    case State.InFile:
                        ProcessInFile(line, blocks, fileBuf, fileBlockName,
                                      ref state, ref fileBlockName,
                                      ref patchFileName, patchBuf, ref patchSec,
                                      scratchBuf, textBuf, ref lastShellCommand,
                                      hasActionableBlocks);
                        break;

                    // ── InPatch ───────────────────────────────────────────────────
                    case State.InPatch:
                        ProcessInPatch(line, blocks, patchBuf, patchFileName,
                                       findBuf, replaceBuf,
                                       ref patchSec, ref state,
                                       ref patchFileName, textBuf, fileBuf,
                                       ref fileBlockName, scratchBuf,
                                       ref lastShellCommand, hasActionableBlocks);
                        break;

                    // ── InScratchpad ──────────────────────────────────────────────
                    case State.InScratchpad:
                        ProcessInScratchpad(line, blocks, scratchBuf, ref state,
                                            ref fileBlockName, fileBuf,
                                            ref patchFileName, patchBuf, ref patchSec,
                                            textBuf, ref lastShellCommand, hasActionableBlocks);
                        break;
                }
            }

            // ── Flush whatever state we are in at end-of-response ─────────────
            switch (state)
            {
                case State.TopLevel:
                    FlushText(blocks, textBuf);
                    break;

                case State.InFile:
                    // Implicit end — emit what we have
                    EmitFileBlock(blocks, fileBlockName,
                                  fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                    fileBuf.Clear();
                    FlushText(blocks, textBuf);
                    break;

                case State.InPatch:
                    // Flush any open FIND/REPLACE pair into patchBuf, then emit PatchBlock
                    FlushOpenPatchPair(patchBuf, findBuf, replaceBuf, patchSec);
                    EmitPatchBlock(blocks, patchFileName,
                                   patchBuf.ToString().TrimEnd('\r', '\n', ' '));
                    patchBuf.Clear();
                    FlushText(blocks, textBuf);
                    break;

                case State.InScratchpad:
                    EmitScratchpadBlock(blocks, scratchBuf);
                    FlushText(blocks, textBuf);
                    break;
            }

            return blocks;
        }

        // ── State handlers ────────────────────────────────────────────────────────

        private static void ProcessTopLevel(
            string line,
            List<ResponseBlock> blocks,
            StringBuilder textBuf,
            ref string lastShellCommand,
            bool hasActionableBlocks,
            ref State state,
            ref string fileBlockName,
            ref string patchFileName,
            StringBuilder patchBuf,
            ref PatchSection patchSec,
            StringBuilder scratchBuf)
        {
            Match m;

            // FILE: <filename>
            m = _fileStart.Match(line);
            if (m.Success)
            {
                FlushText(blocks, textBuf);
                fileBlockName = m.Groups[1].Value;
                state = State.InFile;
                return;
            }

            // PATCH <filename>
            m = _patchStart.Match(line);
            if (m.Success)
            {
                FlushText(blocks, textBuf);
                patchFileName = m.Groups[1].Value;
                patchBuf.Clear();
                patchBuf.AppendLine(line); // include PATCH header in content
                patchSec = PatchSection.Preamble;
                state = State.InPatch;
                return;
            }

            // SCRATCHPAD:
            if (_scratchpadStart.IsMatch(line))
            {
                FlushText(blocks, textBuf);
                scratchBuf.Clear();
                state = State.InScratchpad;
                return;
            }

            // SHELL: <command>
            m = _shellLine.Match(line);
            if (m.Success)
            {
                FlushText(blocks, textBuf);
                string cmd = m.Groups[1].Value.Trim();
                if (!string.Equals(cmd, lastShellCommand, StringComparison.Ordinal))
                {
                    blocks.Add(new ResponseBlock { Type = BlockType.Shell, Command = cmd });
                    lastShellCommand = cmd;
                }
                return; // stay in TopLevel
            }

            // READ! <filename>  (checked before plain READ)
            m = _forceReadLine.Match(line);
            if (m.Success && !hasActionableBlocks)
            {
                FlushText(blocks, textBuf);
                blocks.Add(new ResponseBlock
                {
                    Type          = BlockType.ReadRequest,
                    FileName      = m.Groups[1].Value,
                    ForceFullRead = true
                });
                return;
            }

            // READ <filename>[:range]
            m = _readLine.Match(line);
            if (m.Success && !hasActionableBlocks)
            {
                FlushText(blocks, textBuf);
                int rangeStart = 0, rangeEnd = 0;
                if (m.Groups[2].Success)
                {
                    int.TryParse(m.Groups[2].Value, out rangeStart);
                    rangeEnd = m.Groups[3].Success
                        ? (int.TryParse(m.Groups[3].Value, out int re) ? re : rangeStart)
                        : rangeStart;
                }
                blocks.Add(new ResponseBlock
                {
                    Type       = BlockType.ReadRequest,
                    FileName   = m.Groups[1].Value,
                    RangeStart = rangeStart,
                    RangeEnd   = rangeEnd
                });
                return;
            }

            // GREP: "pattern" filename[:range]
            m = _grepLine.Match(line);
            if (m.Success && !hasActionableBlocks)
            {
                FlushText(blocks, textBuf);
                int grepStart = 0, grepEnd = 0;
                if (m.Groups[3].Success)
                {
                    int.TryParse(m.Groups[3].Value, out grepStart);
                    grepEnd = m.Groups[4].Success
                        ? (int.TryParse(m.Groups[4].Value, out int ge) ? ge : grepStart)
                        : grepStart;
                }
                blocks.Add(new ResponseBlock
                {
                    Type       = BlockType.Grep,
                    Pattern    = m.Groups[1].Value,
                    FileName   = m.Groups[2].Value,
                    RangeStart = grepStart,
                    RangeEnd   = grepEnd
                });
                return;
            }

            // FIND: "pattern" glob[:range]
            m = _findFileLine.Match(line);
            if (m.Success && !hasActionableBlocks)
            {
                FlushText(blocks, textBuf);
                int findStart = 0, findEnd = 0;
                if (m.Groups[3].Success)
                {
                    int.TryParse(m.Groups[3].Value, out findStart);
                    findEnd = m.Groups[4].Success
                        ? (int.TryParse(m.Groups[4].Value, out int fe) ? fe : findStart)
                        : findStart;
                }
                blocks.Add(new ResponseBlock
                {
                    Type        = BlockType.Find,
                    Pattern     = m.Groups[1].Value,
                    GlobPattern = m.Groups[2].Value,
                    RangeStart  = findStart,
                    RangeEnd    = findEnd
                });
                return;
            }

            // DELETE <filename>
            m = _deleteLine.Match(line);
            if (m.Success)
            {
                FlushText(blocks, textBuf);
                blocks.Add(new ResponseBlock { Type = BlockType.Delete, FileName = m.Groups[1].Value });
                return;
            }

            // RENAME <oldFilename> <newFilename>
            m = _renameLine.Match(line);
            if (m.Success)
            {
                FlushText(blocks, textBuf);
                blocks.Add(new ResponseBlock
                {
                    Type       = BlockType.Rename,
                    RenameFrom = m.Groups[1].Value,
                    RenameTo   = m.Groups[2].Value
                });
                return;
            }

            // DONE
            if (_doneLine.IsMatch(line))
            {
                FlushText(blocks, textBuf);
                blocks.Add(new ResponseBlock { Type = BlockType.Done });
                return;
            }

            // Plain text
            textBuf.AppendLine(line);
        }

        private static void ProcessInFile(
            string line,
            List<ResponseBlock> blocks,
            StringBuilder fileBuf,
            string currentFileName,
            ref State state,
            ref string fileBlockName,
            ref string patchFileName,
            StringBuilder patchBuf,
            ref PatchSection patchSec,
            StringBuilder scratchBuf,
            StringBuilder textBuf,
            ref string lastShellCommand,
            bool hasActionableBlocks)
        {
            // END_FILE — explicit terminator
            if (_fileEnd.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                state = State.TopLevel;
                return;
            }

            // New FILE: — implicit termination, start new file block
            Match m = _fileStart.Match(line);
            if (m.Success)
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                fileBlockName = m.Groups[1].Value;
                // state stays InFile with new filename
                return;
            }

            // PATCH — implicit termination, transition to InPatch
            m = _patchStart.Match(line);
            if (m.Success)
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                patchFileName = m.Groups[1].Value;
                patchBuf.Clear();
                patchBuf.AppendLine(line);
                patchSec = PatchSection.Preamble;
                state = State.InPatch;
                return;
            }

            // SCRATCHPAD: — implicit termination
            if (_scratchpadStart.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                scratchBuf.Clear();
                state = State.InScratchpad;
                return;
            }

            // SHELL: — implicit termination, emit shell block, return to TopLevel
            m = _shellLine.Match(line);
            if (m.Success)
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                string cmd = m.Groups[1].Value.Trim();
                if (!string.Equals(cmd, lastShellCommand, StringComparison.Ordinal))
                {
                    blocks.Add(new ResponseBlock { Type = BlockType.Shell, Command = cmd });
                    lastShellCommand = cmd;
                }
                state = State.TopLevel;
                return;
            }

            // READ — implicit termination
            bool isRead = (!hasActionableBlocks) &&
                          (_forceReadLine.IsMatch(line) || _readLine.IsMatch(line));
            if (isRead)
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                // Re-process as TopLevel so the READ block is emitted correctly
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // GREP: — implicit termination
            if (!hasActionableBlocks && _grepLine.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // FIND: — implicit termination
            if (!hasActionableBlocks && _findFileLine.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // DONE — implicit termination
            if (_doneLine.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                blocks.Add(new ResponseBlock { Type = BlockType.Done });
                state = State.TopLevel;
                return;
            }

            // DELETE — implicit termination
            if (_deleteLine.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // RENAME — implicit termination
            if (_renameLine.IsMatch(line))
            {
                EmitFileBlock(blocks, currentFileName,
                              fileBuf.ToString().TrimEnd('\r', '\n', ' '));
                fileBuf.Clear();
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // Content line
            fileBuf.AppendLine(line);
        }

        private static void ProcessInPatch(
            string line,
            List<ResponseBlock> blocks,
            StringBuilder patchBuf,
            string currentPatchFile,
            StringBuilder findBuf,
            StringBuilder replaceBuf,
            ref PatchSection patchSec,
            ref State state,
            ref string patchFileName,
            StringBuilder textBuf,
            StringBuilder fileBuf,
            ref string fileBlockName,
            StringBuilder scratchBuf,
            ref string lastShellCommand,
            bool hasActionableBlocks)
        {
            // END_PATCH — explicit terminator (consumed, not added to patchBuf)
            if (_patchEnd.IsMatch(line))
            {
                FlushOpenPatchPair(patchBuf, findBuf, replaceBuf, patchSec);
                EmitPatchBlock(blocks, currentPatchFile,
                               patchBuf.ToString().TrimEnd('\r', '\n', ' '));
                patchBuf.Clear();
                findBuf.Clear();
                replaceBuf.Clear();
                patchSec = PatchSection.Preamble;
                state = State.TopLevel;
                return;
            }

            // New PATCH <filename> — implicit end of current PATCH, start new one
            Match m = _patchStart.Match(line);
            if (m.Success)
            {
                FlushOpenPatchPair(patchBuf, findBuf, replaceBuf, patchSec);
                EmitPatchBlock(blocks, currentPatchFile,
                               patchBuf.ToString().TrimEnd('\r', '\n', ' '));
                patchBuf.Clear();
                findBuf.Clear();
                replaceBuf.Clear();
                patchFileName = m.Groups[1].Value;
                patchBuf.AppendLine(line);
                patchSec = PatchSection.Preamble;
                // state stays InPatch
                return;
            }

            // FIND: — start a new find section
            // (if we were in InReplace, the previous pair is complete — flush it)
            if (_findLine.IsMatch(line))
            {
                if (patchSec == PatchSection.InReplace)
                {
                    // Close previous pair: write FIND/REPLACE into patchBuf
                    patchBuf.AppendLine("FIND:");
                    patchBuf.Append(findBuf);
                    findBuf.Clear();
                    patchBuf.AppendLine("REPLACE:");
                    patchBuf.Append(replaceBuf);
                    replaceBuf.Clear();
                }
                patchSec = PatchSection.InFind;
                findBuf.Clear();
                return; // don't add the FIND: keyword line itself — it gets added when we flush the pair
            }

            // REPLACE: — transition from InFind to InReplace
            if (_replaceLine.IsMatch(line) && patchSec == PatchSection.InFind)
            {
                patchSec = PatchSection.InReplace;
                replaceBuf.Clear();
                return;
            }

            // All other lines are opaque content — FILE:, SHELL:, SCRATCHPAD:, DONE, etc.
            // are NOT directive boundaries inside a PATCH block.
            switch (patchSec)
            {
                case PatchSection.Preamble:
                    // Lines between PATCH header and first FIND: — usually empty or comments.
                    // Write directly into patchBuf.
                    patchBuf.AppendLine(line);
                    break;
                case PatchSection.InFind:
                    findBuf.AppendLine(line);
                    break;
                case PatchSection.InReplace:
                    replaceBuf.AppendLine(line);
                    break;
            }
        }

        private static void ProcessInScratchpad(
            string line,
            List<ResponseBlock> blocks,
            StringBuilder scratchBuf,
            ref State state,
            ref string fileBlockName,
            StringBuilder fileBuf,
            ref string patchFileName,
            StringBuilder patchBuf,
            ref PatchSection patchSec,
            StringBuilder textBuf,
            ref string lastShellCommand,
            bool hasActionableBlocks)
        {
            // END_SCRATCHPAD — explicit terminator
            if (_scratchpadEnd.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                return;
            }

            // Implicit termination: directive keywords at line start cannot be scratchpad content.
            // Re-process the line as TopLevel after emitting the scratchpad block.
            Match m;

            // FILE: <filename>
            m = _fileStart.Match(line);
            if (m.Success)
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                fileBlockName = m.Groups[1].Value;
                fileBuf.Clear();
                state = State.InFile;
                return;
            }

            // PATCH <filename>
            m = _patchStart.Match(line);
            if (m.Success)
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                patchFileName = m.Groups[1].Value;
                patchBuf.Clear();
                patchBuf.AppendLine(line);
                patchSec = PatchSection.Preamble;
                state = State.InPatch;
                return;
            }

            // SHELL: <command>
            m = _shellLine.Match(line);
            if (m.Success)
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                string cmd = m.Groups[1].Value.Trim();
                if (!string.Equals(cmd, lastShellCommand, StringComparison.Ordinal))
                {
                    blocks.Add(new ResponseBlock { Type = BlockType.Shell, Command = cmd });
                    lastShellCommand = cmd;
                }
                state = State.TopLevel;
                return;
            }

            // READ / READ! — implicit termination (only when no actionable blocks)
            bool isRead = !hasActionableBlocks &&
                          (_forceReadLine.IsMatch(line) || _readLine.IsMatch(line));
            if (isRead)
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // GREP: — implicit termination
            if (!hasActionableBlocks && _grepLine.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // FIND: — implicit termination
            if (!hasActionableBlocks && _findFileLine.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // DONE — implicit termination
            if (_doneLine.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                blocks.Add(new ResponseBlock { Type = BlockType.Done });
                state = State.TopLevel;
                return;
            }

            // DELETE — implicit termination
            if (_deleteLine.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // RENAME — implicit termination
            if (_renameLine.IsMatch(line))
            {
                EmitScratchpadBlock(blocks, scratchBuf);
                state = State.TopLevel;
                ProcessTopLevel(line, blocks, textBuf, ref lastShellCommand,
                                hasActionableBlocks, ref state, ref fileBlockName,
                                ref patchFileName, patchBuf, ref patchSec, scratchBuf);
                return;
            }

            // Content line
            scratchBuf.AppendLine(line);
        }

        private static void EmitScratchpadBlock(List<ResponseBlock> blocks, StringBuilder scratchBuf)
        {
            string content = scratchBuf.ToString().TrimEnd('\r', '\n', ' ');
            if (!string.IsNullOrWhiteSpace(content))
                blocks.Add(new ResponseBlock { Type = BlockType.Scratchpad, Content = content });
            scratchBuf.Clear();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Flush any open FIND/REPLACE pair buffers into patchBuf before emitting the PatchBlock.
        /// </summary>
        private static void FlushOpenPatchPair(
            StringBuilder patchBuf,
            StringBuilder findBuf,
            StringBuilder replaceBuf,
            PatchSection patchSec)
        {
            if (patchSec == PatchSection.InFind || patchSec == PatchSection.InReplace)
            {
                patchBuf.AppendLine("FIND:");
                patchBuf.Append(findBuf);
                findBuf.Clear();
                if (patchSec == PatchSection.InReplace)
                {
                    patchBuf.AppendLine("REPLACE:");
                    patchBuf.Append(replaceBuf);
                    replaceBuf.Clear();
                }
            }
        }

        private static void EmitFileBlock(List<ResponseBlock> blocks, string fileName, string content)
        {
            // Second-layer defense: strip any trailing END / END_FILE line that leaked into
            // the buffer due to token boundary splits during streaming.
            int lastNl = content.LastIndexOf('\n');
            string lastLine = lastNl >= 0
                ? content.Substring(lastNl + 1).Trim()
                : content.Trim();
            if (lastLine == "END_FILE" || lastLine == "END" || lastLine.StartsWith("END_FILE"))
                content = content.Substring(0, lastNl < 0 ? 0 : lastNl).TrimEnd('\r', '\n', ' ');

            blocks.Add(new ResponseBlock
            {
                Type     = BlockType.File,
                FileName = fileName,
                Content  = content
            });
        }

        private static void EmitPatchBlock(List<ResponseBlock> blocks, string fileName, string content)
        {
            blocks.Add(new ResponseBlock
            {
                Type     = BlockType.Patch,
                FileName = fileName,
                Content  = content
            });
        }

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
                    System.Diagnostics.Debug.WriteLine($"[PARSER-DIAG] Fence at line {i}: \"{lines[i]}\" nextIsDirective={nextIsDirective} prevIsDirective={prevIsDirective} → {(nextIsDirective || prevIsDirective ? "DROPPED" : "KEPT")}");
                    if (nextIsDirective || prevIsDirective)
                        continue; // drop fence
                }
                result.Add(lines[i]);
            }
            return result.ToArray();
        }

        private static bool HasActionableBlocks(string[] lines)
        {
            bool inPatch = false;
            bool inScratchpad = false;
            foreach (string line in lines)
            {
                // Skip scratchpad content — directive-like lines inside SCRATCHPAD should not
                // suppress READ parsing in the main pass.
                if (_scratchpadStart.IsMatch(line)) { inScratchpad = true;  continue; }
                if (_scratchpadEnd.IsMatch(line))   { inScratchpad = false; continue; }
                if (inScratchpad) continue;

                // Skip PATCH content — FILE: inside a PATCH is not a real file block
                if (_patchStart.IsMatch(line)) { inPatch = true; }
                if (_patchEnd.IsMatch(line))   { inPatch = false; continue; }
                if (inPatch) continue;

                if (_patchStart.IsMatch(line) || _fileStart.IsMatch(line) || _shellLine.IsMatch(line) || _deleteLine.IsMatch(line) || _renameLine.IsMatch(line))
                    return true;
            }
            return false;
        }
    }
}
