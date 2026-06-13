// File: CodeBlockStreamer.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Splits a streamed assistant response into prose and fenced code blocks. Prose is
// forwarded LIVE (char-by-char, so the smooth streaming feel is preserved); a
// ```lang … ``` fenced block is buffered and emitted as one syntax-highlighted unit
// when its closing fence arrives, with the ``` marker lines hidden.
//
// Only the first character of each line is ever held back (to test for a fence
// marker), so prose streaming is effectively unbuffered. Feed visible tokens via
// Feed(); call Flush() once the stream ends to release any tail. Not thread-safe —
// drive it from a single streaming callback (one LLM iteration per instance).

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
        private readonly StringBuilder _codeBuf  = new StringBuilder(); // accumulated body
        private readonly StringBuilder _codeLine = new StringBuilder(); // current (incomplete) code line

        // Prose state.
        private bool _lineClassified;                              // current prose line type known → stream live
        private readonly StringBuilder _prefix = new StringBuilder(); // held line-start while still ambiguous
        private readonly StringBuilder _proseOut = new StringBuilder(); // batched prose for the current Feed

        public CodeBlockStreamer(Action<string> prose, Action<string, string> code)
        {
            _prose = prose;
            _code  = code;
        }

        public void Feed(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text) FeedChar(c);
            FlushProse(); // one prose emit per Feed (≈ one span per SSE token, as before)
        }

        private void FlushProse()
        {
            if (_proseOut.Length == 0) return;
            _prose(_proseOut.ToString());
            _proseOut.Clear();
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
                        // Closing fence — emit the buffered block highlighted, markers dropped.
                        _code(_codeBuf.ToString(), _lang.Trim());
                        _codeBuf.Clear();
                        _inCode = false;
                        _lang = string.Empty;
                        _lineClassified = false;
                        _prefix.Clear();
                    }
                    else
                    {
                        _codeBuf.Append(line).Append('\n');
                    }
                }
                else _codeLine.Append(c);
                return;
            }

            if (_lineClassified)
            {
                // Prose line already classified — accumulate; FlushProse emits per Feed.
                _proseOut.Append(c);
                if (c == '\n') { _lineClassified = false; _prefix.Clear(); }
                return;
            }

            // Line start, still ambiguous — hold until we can tell prose from a fence opener.
            _prefix.Append(c);
            string t  = _prefix.ToString();
            string ts = t.TrimStart();

            if (c == '\n')
            {
                if (ts.StartsWith("```", StringComparison.Ordinal)) OpenFence(ts);
                else _proseOut.Append(t);
                _prefix.Clear();
                _lineClassified = false;
                return;
            }

            if (ts.Length == 0) return; // only leading whitespace so far — keep holding

            if (ts[0] != '`')
            {
                // Ordinary prose line — emit the held prefix; rest streams via _proseOut.
                _proseOut.Append(t);
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
                _proseOut.Append(t);
                _prefix.Clear();
                _lineClassified = true;
            }
            // else keep holding — it may still become ```
        }

        // ts begins with ``` ; the remainder of the line (if present) is the language tag.
        private void OpenFence(string ts)
        {
            FlushProse(); // emit any prose before the code block, in order
            _inCode = true;
            _codeBuf.Clear();
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
                string remaining = _codeBuf.ToString() + _codeLine.ToString();
                if (remaining.Length > 0) _code(remaining, _lang.Trim());
                _codeBuf.Clear();
                _codeLine.Clear();
                _inCode = false;
                _collectingLang = false;
                _lang = string.Empty;
            }
            else if (_prefix.Length > 0)
            {
                _proseOut.Append(_prefix.ToString());
                _prefix.Clear();
                FlushProse();
            }
        }
    }
}
