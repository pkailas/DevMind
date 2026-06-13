// File: SyntaxHighlighter.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Lightweight, dependency-free source tokenizer for the TUI transcript. Produces
// a flat list of (text, kind) tokens whose concatenated Text equals the input
// EXACTLY (whitespace, newlines, and punctuation are preserved as Plain tokens),
// so the renderer can paint each token its own color without disturbing layout.
//
// Two lexers: a precise C# lexer and a generic fallback (strings / comments /
// numbers / a cross-language keyword set). The host (TuiAgenticHost) maps
// TokenKind → RGB (VS Code Dark+ palette) and appends one colored span per token.

using System;
using System.Collections.Generic;
using System.IO;

namespace DevMind
{
    /// <summary>Semantic role of a source token — mapped to a color by the renderer.</summary>
    public enum TokenKind
    {
        Plain,          // identifiers, punctuation, whitespace
        Keyword,        // declaration / modifier / type keywords (blue)
        ControlKeyword, // control-flow keywords (purple)
        Type,           // PascalCase type references (teal)
        Method,         // identifier immediately followed by '(' (yellow)
        StringLit,      // string / char / verbatim / interpolated (orange)
        Comment,        // // , /// , /* */ , # (green)
        Number,         // numeric literals (pale green)
    }

    /// <summary>One highlighted token. <see cref="Text"/> is a verbatim slice of the input.</summary>
    public readonly struct SyntaxToken
    {
        public readonly string Text;
        public readonly TokenKind Kind;
        public SyntaxToken(string text, TokenKind kind) { Text = text; Kind = kind; }
    }

    /// <summary>Dependency-free tokenizer for transcript syntax highlighting.</summary>
    public static class SyntaxHighlighter
    {
        // C# declaration / modifier / built-in type keywords → blue.
        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","async","await","base","bool","byte","char","class","const","decimal",
            "delegate","double","enum","event","explicit","extern","false","fixed","float","get",
            "implicit","in","int","interface","internal","is","long","namespace","null","object",
            "operator","out","override","params","partial","private","protected","public","readonly",
            "ref","sbyte","sealed","set","short","static","string","struct","this","true","uint",
            "ulong","unsafe","ushort","using","value","var","virtual","void","volatile","where","with",
            "nameof","typeof","sizeof","stackalloc","global","record","init","required","scoped","add","remove",
        };

        // C# control-flow keywords → purple.
        private static readonly HashSet<string> CSharpControl = new HashSet<string>(StringComparer.Ordinal)
        {
            "break","case","catch","continue","default","do","else","finally","for","foreach","goto",
            "if","lock","new","return","switch","throw","try","while","yield","when","checked","unchecked","select","from",
        };

        // Generic cross-language keyword set for the fallback lexer.
        private static readonly HashSet<string> GenericKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","else","for","while","do","return","function","def","class","import","from","export",
            "const","let","var","new","public","private","protected","static","void","int","string","bool",
            "true","false","null","none","nil","undefined","async","await","try","catch","finally","throw",
            "switch","case","break","continue","interface","enum","struct","type","package","func","fn",
            "in","of","is","as","with","yield","lambda","print","echo","local","end","then","elif","not","and","or",
        };

        /// <summary>Tokenize <paramref name="code"/> for the given language id
        /// ("csharp"/"cs" use the precise lexer; anything else uses the generic one).</summary>
        public static List<SyntaxToken> Highlight(string code, string language)
        {
            if (string.IsNullOrEmpty(code)) return new List<SyntaxToken>();
            string lang = (language ?? string.Empty).Trim().ToLowerInvariant();
            return lang == "csharp" || lang == "cs" || lang == "c#"
                ? HighlightCSharp(code)
                : HighlightGeneric(code, lang);
        }

        /// <summary>Maps a file path/extension to a language id understood by <see cref="Highlight"/>.</summary>
        public static string LanguageFromExtension(string path)
        {
            string ext = (Path.GetExtension(path ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".cs": return "csharp";
                case ".ts": case ".tsx": return "typescript";
                case ".js": case ".jsx": case ".mjs": case ".cjs": return "javascript";
                case ".py": return "python";
                case ".json": return "json";
                case ".sh": case ".bash": return "shell";
                case ".ps1": return "powershell";
                case ".java": return "java";
                case ".go": return "go";
                case ".rs": return "rust";
                case ".cpp": case ".cc": case ".cxx": case ".h": case ".hpp": case ".c": return "cpp";
                case ".xml": case ".csproj": case ".props": case ".targets": return "xml";
                case ".sql": return "sql";
                default: return "text";
            }
        }

        // ── C# lexer ──────────────────────────────────────────────────────────────
        private static List<SyntaxToken> HighlightCSharp(string s)
        {
            var toks = new List<SyntaxToken>();
            int i = 0, n = s.Length;

            while (i < n)
            {
                char c = s[i];

                // Whitespace run.
                if (char.IsWhiteSpace(c))
                {
                    int j = i + 1;
                    while (j < n && char.IsWhiteSpace(s[j])) j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Plain));
                    i = j; continue;
                }

                // Line comment (// and ///).
                if (c == '/' && i + 1 < n && s[i + 1] == '/')
                {
                    int j = i + 2;
                    while (j < n && s[j] != '\n') j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Comment));
                    i = j; continue;
                }

                // Block comment /* ... */.
                if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    int j = i + 2;
                    while (j < n && !(s[j] == '*' && j + 1 < n && s[j + 1] == '/')) j++;
                    j = Math.Min(n, j + 2);
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Comment));
                    i = j; continue;
                }

                // Verbatim string @"...""..."
                if (c == '@' && i + 1 < n && s[i + 1] == '"')
                {
                    int j = i + 2;
                    while (j < n)
                    {
                        if (s[j] == '"')
                        {
                            if (j + 1 < n && s[j + 1] == '"') { j += 2; continue; }
                            j++; break;
                        }
                        j++;
                    }
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.StringLit));
                    i = j; continue;
                }

                // Interpolated / normal string $"..." or "..." (escape-aware, single line-ish).
                if (c == '"' || (c == '$' && i + 1 < n && s[i + 1] == '"'))
                {
                    int j = c == '$' ? i + 2 : i + 1;
                    while (j < n && s[j] != '"' && s[j] != '\n')
                    {
                        if (s[j] == '\\') j += 2; else j++;
                    }
                    if (j < n && s[j] == '"') j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.StringLit));
                    i = j; continue;
                }

                // Char literal '...'.
                if (c == '\'')
                {
                    int j = i + 1;
                    while (j < n && s[j] != '\'' && s[j] != '\n')
                    {
                        if (s[j] == '\\') j += 2; else j++;
                    }
                    if (j < n && s[j] == '\'') j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.StringLit));
                    i = j; continue;
                }

                // Number.
                if (char.IsDigit(c))
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Number));
                    i = j; continue;
                }

                // Identifier / keyword.
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    string word = s.Substring(i, j - i);
                    TokenKind kind;
                    if (CSharpControl.Contains(word)) kind = TokenKind.ControlKeyword;
                    else if (CSharpKeywords.Contains(word)) kind = TokenKind.Keyword;
                    else if (j < n && s[j] == '(') kind = TokenKind.Method;
                    else if (char.IsUpper(word[0])) kind = TokenKind.Type;
                    else kind = TokenKind.Plain;
                    toks.Add(new SyntaxToken(word, kind));
                    i = j; continue;
                }

                // Any other single char (punctuation / operators).
                toks.Add(new SyntaxToken(s.Substring(i, 1), TokenKind.Plain));
                i++;
            }

            return toks;
        }

        // ── Generic fallback lexer ────────────────────────────────────────────────
        private static List<SyntaxToken> HighlightGeneric(string s, string lang)
        {
            // '#' starts a line comment in shell/python/ruby/etc., but is a directive
            // in C-family — enable it only for languages where it is a comment.
            bool hashComment = lang == "python" || lang == "shell" || lang == "powershell"
                || lang == "ruby" || lang == "yaml" || lang == "toml" || lang == "text";

            var toks = new List<SyntaxToken>();
            int i = 0, n = s.Length;

            while (i < n)
            {
                char c = s[i];

                if (char.IsWhiteSpace(c))
                {
                    int j = i + 1;
                    while (j < n && char.IsWhiteSpace(s[j])) j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Plain));
                    i = j; continue;
                }

                if ((c == '/' && i + 1 < n && s[i + 1] == '/') || (hashComment && c == '#'))
                {
                    int j = (c == '#') ? i + 1 : i + 2;
                    while (j < n && s[j] != '\n') j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Comment));
                    i = j; continue;
                }

                if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    int j = i + 2;
                    while (j < n && !(s[j] == '*' && j + 1 < n && s[j + 1] == '/')) j++;
                    j = Math.Min(n, j + 2);
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Comment));
                    i = j; continue;
                }

                if (c == '"' || c == '\'' || c == '`')
                {
                    char q = c;
                    int j = i + 1;
                    while (j < n && s[j] != q && s[j] != '\n')
                    {
                        if (s[j] == '\\') j += 2; else j++;
                    }
                    if (j < n && s[j] == q) j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.StringLit));
                    i = j; continue;
                }

                if (char.IsDigit(c))
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
                    toks.Add(new SyntaxToken(s.Substring(i, j - i), TokenKind.Number));
                    i = j; continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$')) j++;
                    string word = s.Substring(i, j - i);
                    TokenKind kind;
                    if (GenericKeywords.Contains(word)) kind = TokenKind.Keyword;
                    else if (j < n && s[j] == '(') kind = TokenKind.Method;
                    else if (char.IsUpper(word[0])) kind = TokenKind.Type;
                    else kind = TokenKind.Plain;
                    toks.Add(new SyntaxToken(word, kind));
                    i = j; continue;
                }

                toks.Add(new SyntaxToken(s.Substring(i, 1), TokenKind.Plain));
                i++;
            }

            return toks;
        }
    }
}
