// File: CodeBlockStreamer.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Splits a streamed assistant response into prose and fenced code blocks, rendering
// both LIVE so the transcript never stalls. Prose is emitted per COMPLETED line (the
// same one-line-of-latency trade as code); inside a ```lang … ``` block each code
// line is emitted syntax-highlighted the moment it completes, with the ``` marker
// lines hidden. Per-line emission means a multi-line construct (block comment,
// verbatim string) is colored per line — a minor cosmetic trade for continuous
// feedback.
//
// Line buffering is what makes inline-span styling possible downstream: a
// **bold** or `code` pair can arrive split across two Feed calls, so a run is only
// complete once the line is. The prose callback receives the line INCLUDING its
// terminating newline (a trailing partial line at Flush() carries none). Only the
// first characters of a line are ever held back while it is still ambiguous with a
// fence marker. Feed visible tokens via Feed(); call Flush() once the stream ends
// to release any tail. Not thread-safe — drive it from a single streaming callback
// (one LLM iteration per instance).

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    internal sealed class CodeBlockStreamer
    {
        // A line that is only backticks (3+) and optional trailing whitespace = a fence marker.
        private static readonly Regex FenceLine = new Regex(@"^`{3,}\s*$", RegexOptions.Compiled);

        private readonly Action<string> _prose;        // emit prose (rendered Normal/live)
        private readonly Action<string, string> _code; // emit a finished code block (text, language)

        // Code-block state.
        private bool _inCode;
        private bool _collectingLang;                              // reading the language tag after ```
        private string _lang = string.Empty;
        private readonly StringBuilder _codeLine = new StringBuilder(); // current (incomplete) code line

        // Prose state.
        private bool _lineClassified;                              // current prose line type known → buffer whole line
        private readonly StringBuilder _prefix = new StringBuilder(); // held line-start while still ambiguous
        private readonly StringBuilder _proseLine = new StringBuilder(); // the current prose line, incl. its newline

        public CodeBlockStreamer(Action<string> prose, Action<string, string> code)
        {
            _prose = prose;
            _code  = code;
        }

        public void Feed(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text) FeedChar(c);
            // Prose emits happen inside FeedChar, at the newline that completes a line.
        }

        private void FeedChar(char c)
        {
            if (_inCode)
            {
                if (_collectingLang)
                {
                    if (c == '\n') _collectingLang = false; // body starts on the next line
                    else _lang += c;
                    return;
                }

                if (c == '\n')
                {
                    string line = _codeLine.ToString();
                    _codeLine.Clear();
                    if (FenceLine.IsMatch(line.Trim()))
                    {
                        // Closing fence — hide the marker and leave code mode.
                        _inCode = false;
                        _lang = string.Empty;
                        _lineClassified = false;
                        _prefix.Clear();
                    }
                    else
                    {
                        // Emit this code line highlighted immediately — live, no block buffering.
                        _code(line + "\n", _lang.Trim());
                    }
                }
                else _codeLine.Append(c);
                return;
            }

            if (_lineClassified)
            {
                // Prose line already classified — buffer until the newline completes it.
                _proseLine.Append(c);
                if (c == '\n') EmitProseLine();
                return;
            }

            // Line start, still ambiguous — hold until we can tell prose from a fence opener.
            _prefix.Append(c);
            string t  = _prefix.ToString();
            string ts = t.TrimStart();

            if (c == '\n')
            {
                if (ts.StartsWith("```", StringComparison.Ordinal)) OpenFence(ts);
                else EmitProseLine(t);
                _prefix.Clear();
                _lineClassified = false;
                return;
            }

            if (ts.Length == 0) return; // only leading whitespace so far — keep holding

            if (ts[0] != '`')
            {
                // Ordinary prose line — buffer the held prefix; rest follows until the newline.
                _proseLine.Append(t);
                _prefix.Clear();
                _lineClassified = true;
                return;
            }

            if (ts.StartsWith("```", StringComparison.Ordinal))
            {
                OpenFence(ts);
                _prefix.Clear();
                return;
            }

            // 1-2 backticks; if a third char arrived that isn't a backtick it's inline code → prose.
            if (ts.Length >= 3)
            {
                _proseLine.Append(t);
                _prefix.Clear();
                _lineClassified = true;
            }
            // else keep holding — it may still become ```
        }

        // Emit one completed prose line. `text` is the full line INCLUDING its terminating
        // newline (the renderer and host expect that; a trailing partial line at Flush()
        // carries no newline).
        private void EmitProseLine(string text)
        {
            if (text.Length == 0) return;
            _prose(text);
        }

        // The classified path: the newline just completed the line buffered in _proseLine.
        private void EmitProseLine()
        {
            EmitProseLine(_proseLine.ToString());
            _proseLine.Clear();
            _lineClassified = false;
            _prefix.Clear();
        }

        // ts begins with ``` ; the remainder of the line (if present) is the language tag.
        private void OpenFence(string ts)
        {
            // Any prose is already complete-line at this point; nothing is buffered un-emitted.
            _inCode = true;
            _codeLine.Clear();

            string after = ts.Substring(3);
            int nl = after.IndexOf('\n');
            if (nl >= 0)
            {
                _lang = after.Substring(0, nl);
                _collectingLang = false; // language already complete (the line ended)
            }
            else
            {
                _lang = after;
                _collectingLang = true;  // keep appending until the newline
            }
        }

        /// <summary>Release any buffered tail when the stream ends (or is cancelled):
        /// an unterminated code block is emitted highlighted; a held prose prefix is flushed.</summary>
        public void Flush()
        {
            if (_inCode)
            {
                // Unterminated block — emit the partial last line so nothing is lost.
                if (_codeLine.Length > 0) _code(_codeLine.ToString(), _lang.Trim());
                _codeLine.Clear();
                _inCode = false;
                _collectingLang = false;
                _lang = string.Empty;
            }
            else
            {
                // Trailing partial prose line (no terminating newline) — release it so
                // nothing is lost.
                EmitProseLine(_prefix.Length > 0 ? _prefix + _proseLine.ToString() : _proseLine.ToString());
                _prefix.Clear();
                _proseLine.Clear();
                _lineClassified = false;
            }
        }
    }
}
