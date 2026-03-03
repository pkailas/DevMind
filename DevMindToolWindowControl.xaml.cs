// File: DevMindToolWindowControl.xaml.cs  v1.7
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DevMind
{
    /// <summary>
    /// WPF control for the DevMind chat tool window.
    /// Displays a scrollable message history, text input, and status bar.
    /// AI responses are parsed for fenced code blocks and rendered as styled code boxes.
    /// </summary>
    public partial class DevMindToolWindowControl : UserControl
    {
        private readonly LlmClient _llmClient;
        private readonly ObservableCollection<object> _messages;
        private CancellationTokenSource _cts;
        private bool _suppressSystemPromptSave;

        /// <summary>
        /// Initializes a new instance of the <see cref="DevMindToolWindowControl"/> class.
        /// </summary>
        /// <param name="llmClient">The LLM client used to communicate with the AI endpoint.</param>
        public DevMindToolWindowControl(LlmClient llmClient)
        {
            InitializeComponent();
            Themes.SetUseVsTheme(this, true);
            _llmClient = llmClient;
            _messages = new ObservableCollection<object>();
            MessagesPanel.ItemsSource = _messages;

            LoadSystemPromptText();
            DevMindOptions.Saved += OnSettingsSaved;
        }

        /// <summary>
        /// Updates the status bar text.
        /// </summary>
        /// <param name="status">The status text to display.</param>
        public void SetStatus(string status) => StatusText.Text = status;

        private void OnSettingsSaved(DevMindOptions options)
        {
            _llmClient.Configure(options.EndpointUrl, options.ApiKey);
            LoadSystemPromptText();
            TestConnectionInBackground();
        }

        private void LoadSystemPromptText()
        {
            _suppressSystemPromptSave = true;
            SystemPromptTextBox.Text = DevMindOptions.Instance.SystemPrompt ?? "";
            _suppressSystemPromptSave = false;
        }

        private void SystemPromptToggle_Checked(object sender, RoutedEventArgs e)
        {
            SystemPromptPanel.Visibility = Visibility.Visible;
            SystemPromptToggle.Content = "System Prompt \u25B2";
        }

        private void SystemPromptToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            SystemPromptPanel.Visibility = Visibility.Collapsed;
            SystemPromptToggle.Content = "System Prompt \u25BC";
        }

        private void SystemPromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSystemPromptSave)
                return;

            DevMindOptions.Instance.SystemPrompt = SystemPromptTextBox.Text;
            DevMindOptions.Instance.Save();
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                SendMessage();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                int caretIndex = InputTextBox.CaretIndex;
                InputTextBox.Text = InputTextBox.Text.Insert(caretIndex, Environment.NewLine);
                InputTextBox.CaretIndex = caretIndex + Environment.NewLine.Length;
                e.Handled = true;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _messages.Clear();
            _llmClient.ClearHistory();
            StatusText.Text = "Cleared";
        }

        private void SendMessage()
        {
            string text = InputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            InputTextBox.Text = "";
            SetInputEnabled(false);

            _messages.Add(new UserMessageViewModel { Text = text });
            ScrollToBottom();

            var aiMessage = new AssistantMessageViewModel();
            _messages.Add(aiMessage);

            StatusText.Text = "Thinking...";
            _cts = new CancellationTokenSource();

#pragma warning disable VSSDK007 // Fire-and-forget from UI event handler is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                await _llmClient.SendMessageAsync(
                    text,
                    onToken: token =>
                    {
                        ThreadHelper.JoinableTaskFactory.Run(async delegate
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            aiMessage.AddToken(token);
                            ScrollToBottom();
                        });
                    },
                    onComplete: () =>
                    {
                        ThreadHelper.JoinableTaskFactory.Run(async delegate
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            StatusText.Text = "Connected";
                            SetInputEnabled(true);
                            InputTextBox.Focus();
                        });
                    },
                    onError: ex =>
                    {
                        ThreadHelper.JoinableTaskFactory.Run(async delegate
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            aiMessage.AddToken($"\n[Error: {ex.Message}]");
                            StatusText.Text = "Error";
                            SetInputEnabled(true);
                        });
                    },
                    _cts.Token);
            });
        }

        private void SetInputEnabled(bool enabled)
        {
            InputTextBox.IsEnabled = enabled;
            SendButton.IsEnabled = enabled;
        }

        private void ScrollToBottom() => ChatScrollViewer.ScrollToEnd();

        private void TestConnectionInBackground()
        {
#pragma warning disable VSSDK007 // Fire-and-forget from settings change handler is intentional
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
#pragma warning restore VSSDK007
            {
                bool connected = await _llmClient.TestConnectionAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText.Text = connected ? "Connected" : "Disconnected";
            });
        }
    }

    // ── Message view models ──────────────────────────────────────────────────

    /// <summary>
    /// View model for a user message displayed as a right-aligned blue bubble.
    /// </summary>
    public class UserMessageViewModel
    {
        /// <summary>The message text content.</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// View model for an AI assistant response. Accumulates streaming tokens and
    /// re-parses the raw text into alternating text and code segments on each token.
    /// </summary>
    public class AssistantMessageViewModel
    {
        private string _rawText = "";

        /// <summary>Ordered list of text and code segments parsed from the raw response.</summary>
        public ObservableCollection<MessageSegmentViewModel> Segments { get; }
            = new ObservableCollection<MessageSegmentViewModel>();

        /// <summary>
        /// Appends a streaming token to the raw text and re-parses segments.
        /// Must be called on the UI thread.
        /// </summary>
        public void AddToken(string token)
        {
            _rawText += token;
            ReparseSegments();
        }

        private static string StripThinkBlocks(string text)
        {
            // Remove complete <think>...</think> blocks (including any content / newlines).
            var result = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
            // Remove a partial/unclosed <think> block that is still streaming.
            int openIdx = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
                result = result.Substring(0, openIdx);
            return result;
        }

        private void ReparseSegments()
        {
            var parsed = ParseSegments(StripThinkBlocks(_rawText));

            for (int i = 0; i < parsed.Count; i++)
            {
                var (isCode, lang, content) = parsed[i];

                if (i < Segments.Count)
                {
                    // Update existing segment if the type still matches.
                    if (isCode && Segments[i] is CodeSegmentViewModel csv)
                    {
                        csv.Language = lang;
                        csv.Code = content;
                    }
                    else if (!isCode && Segments[i] is TextSegmentViewModel tsv)
                    {
                        tsv.Text = content;
                    }
                    else
                    {
                        // Type changed (e.g. partial fence resolved) — replace in place.
                        Segments[i] = isCode
                            ? (MessageSegmentViewModel)new CodeSegmentViewModel { Language = lang, Code = content }
                            : new TextSegmentViewModel { Text = content };
                    }
                }
                else
                {
                    Segments.Add(isCode
                        ? (MessageSegmentViewModel)new CodeSegmentViewModel { Language = lang, Code = content }
                        : new TextSegmentViewModel { Text = content });
                }
            }

            // Trim segments that are no longer in the parsed result.
            while (Segments.Count > parsed.Count)
                Segments.RemoveAt(Segments.Count - 1);
        }

        /// <summary>
        /// Parses <paramref name="raw"/> into alternating text/code segments.
        /// An unclosed opening fence at the end of the string is treated as literal
        /// text until the closing fence arrives in a subsequent token.
        /// </summary>
        private static List<(bool IsCode, string Language, string Content)> ParseSegments(string raw)
        {
            var result = new List<(bool, string, string)>();
            int pos = 0;
            bool inCode = false;
            string currentLang = "";

            while (pos <= raw.Length)
            {
                int idx = raw.IndexOf("```", pos);

                if (idx < 0)
                {
                    // No more fences — emit the rest as the current segment type.
                    string remaining = raw.Substring(pos);
                    if (remaining.Length > 0)
                        result.Add((inCode, currentLang, remaining));
                    break;
                }

                // Emit content before the fence.
                if (idx > pos)
                    result.Add((inCode, currentLang, raw.Substring(pos, idx - pos)));

                pos = idx + 3; // skip past ```

                if (!inCode)
                {
                    // Opening fence — extract the language identifier (up to the first newline).
                    int nlIdx = raw.IndexOf('\n', pos);
                    if (nlIdx >= 0)
                    {
                        currentLang = raw.Substring(pos, nlIdx - pos).Trim();
                        pos = nlIdx + 1;
                        inCode = true;
                    }
                    else
                    {
                        // The language line hasn't arrived yet (still streaming).
                        // Treat the ``` and partial language as literal text for now;
                        // the next token will re-parse from scratch and resolve it.
                        string partial = "```" + raw.Substring(pos);
                        if (result.Count > 0 && !result[result.Count - 1].Item1)
                        {
                            var last = result[result.Count - 1];
                            result[result.Count - 1] = (false, last.Item2, last.Item3 + partial);
                        }
                        else
                        {
                            result.Add((false, "", partial));
                        }
                        break;
                    }
                }
                else
                {
                    // Closing fence.
                    inCode = false;
                    currentLang = "";
                }
            }

            return result;
        }
    }

    // ── Segment view models ──────────────────────────────────────────────────

    /// <summary>Base class for message segments (text or code).</summary>
    public abstract class MessageSegmentViewModel : INotifyPropertyChanged
    {
        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raises <see cref="PropertyChanged"/> for the given property.</summary>
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A plain-text segment rendered as a styled bubble.</summary>
    public class TextSegmentViewModel : MessageSegmentViewModel
    {
        private string _text;

        /// <summary>The text content.</summary>
        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }
    }

    /// <summary>
    /// A fenced code block segment rendered with a dark background, monospace font,
    /// and a header bar that shows the language and a Copy button.
    /// </summary>
    public class CodeSegmentViewModel : MessageSegmentViewModel
    {
        private string _language;
        private string _code;

        /// <summary>The language identifier from the opening fence (may be empty).</summary>
        public string Language
        {
            get => _language;
            set { _language = value; OnPropertyChanged(); }
        }

        /// <summary>The code content (without the backtick delimiters).</summary>
        public string Code
        {
            get => _code;
            set { _code = value; OnPropertyChanged(); }
        }

        /// <summary>Copies <see cref="Code"/> to the system clipboard.</summary>
        public ICommand CopyCommand { get; }

        public CodeSegmentViewModel()
        {
            CopyCommand = new RelayCommand(() => Clipboard.SetText(_code ?? ""));
        }
    }

    // ── Infrastructure ───────────────────────────────────────────────────────

    /// <summary>Minimal synchronous relay command.</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    /// <summary>
    /// Converts a width value to a percentage of that width.
    /// ConverterParameter specifies the fraction (e.g., 0.9 for 90%).
    /// </summary>
    public class WidthPercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width && parameter != null
                && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                return width * pct;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // ── Markdown rendering ───────────────────────────────────────────────────

    /// <summary>
    /// A read-only <see cref="RichTextBox"/> that renders a markdown string as a
    /// WPF <see cref="FlowDocument"/> using only standard WPF primitives — no external
    /// libraries. Text is fully selectable. Responds to binding updates by regenerating
    /// its <see cref="FlowDocument"/> on each change.
    /// </summary>
    public class MarkdownRichTextBox : RichTextBox
    {
        /// <summary>Dependency property for the markdown source text.</summary>
        public static readonly DependencyProperty MarkdownTextProperty =
            DependencyProperty.Register(
                nameof(MarkdownText),
                typeof(string),
                typeof(MarkdownRichTextBox),
                new PropertyMetadata(null, OnMarkdownTextChanged));

        /// <summary>The markdown text to render.</summary>
        public string MarkdownText
        {
            get => (string)GetValue(MarkdownTextProperty);
            set => SetValue(MarkdownTextProperty, value);
        }

        public MarkdownRichTextBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
        }

        private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((MarkdownRichTextBox)d).Document = MarkdownRenderer.BuildDocument((string)e.NewValue ?? "");
    }

    /// <summary>
    /// Parses a markdown string and produces a WPF <see cref="FlowDocument"/>
    /// containing <see cref="Paragraph"/> blocks with inline <see cref="Run"/>,
    /// <see cref="Bold"/>, <see cref="Italic"/>, and <see cref="Span"/> elements.
    /// Supports: ## h2, ### h3, **bold**, *italic*, `inline code`,
    /// - / * bullet lists, 1. numbered lists, --- horizontal rules, plain text.
    /// </summary>
    internal static class MarkdownRenderer
    {
        internal static FlowDocument BuildDocument(string markdown)
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            foreach (var line in markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                doc.Blocks.Add(BuildBlock(line));
            return doc;
        }

        private static Block BuildBlock(string line)
        {
            // ### Heading 3 — check before ## to avoid prefix collision
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var p = new Paragraph { FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) };
                AddInlines(p.Inlines, line.Substring(4));
                return p;
            }

            // ## Heading 2
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var p = new Paragraph { FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) };
                AddInlines(p.Inlines, line.Substring(3));
                return p;
            }

            // Horizontal rule: three or more dashes and nothing else
            if (line.Length >= 3 && IsAllDashes(line))
            {
                return new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                    Margin = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                });
            }

            // Bullet list: "- text" or "* text"
            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal))
            {
                var p = new Paragraph { Margin = new Thickness(16, 0, 0, 0) };
                p.Inlines.Add(new Run("\u2022 "));
                AddInlines(p.Inlines, line.Substring(2));
                return p;
            }

            // Numbered list: "1. text", "12. text", etc.
            if (TryParseNumberedList(line, out string numPrefix, out string numContent))
            {
                var p = new Paragraph { Margin = new Thickness(16, 0, 0, 0) };
                p.Inlines.Add(new Run(numPrefix));
                AddInlines(p.Inlines, numContent);
                return p;
            }

            // Empty line → compact spacer paragraph
            if (line.Length == 0)
                return new Paragraph { Margin = new Thickness(0), LineHeight = 6 };

            // Plain paragraph
            var plain = new Paragraph { Margin = new Thickness(0) };
            AddInlines(plain.Inlines, line);
            return plain;
        }

        private static bool IsAllDashes(string line)
        {
            foreach (char c in line)
                if (c != '-') return false;
            return true;
        }

        private static bool TryParseNumberedList(string line, out string prefix, out string content)
        {
            int dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot <= 3)
            {
                bool allDigits = true;
                for (int i = 0; i < dot; i++)
                    if (!char.IsDigit(line[i])) { allDigits = false; break; }

                if (allDigits)
                {
                    prefix = line.Substring(0, dot + 2);
                    content = line.Substring(dot + 2);
                    return true;
                }
            }
            prefix = content = null;
            return false;
        }

        /// <summary>
        /// Parses inline markdown (**bold**, *italic*, `code`) and appends
        /// <see cref="Inline"/> elements to <paramref name="inlines"/>.
        /// </summary>
        private static void AddInlines(InlineCollection inlines, string text)
        {
            int pos = 0;
            var sb = new StringBuilder();

            while (pos < text.Length)
            {
                char c = text[pos];

                // **bold**
                if (c == '*' && pos + 1 < text.Length && text[pos + 1] == '*')
                {
                    FlushPlain(inlines, sb);
                    int end = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        inlines.Add(new Bold(new Run(text.Substring(pos + 2, end - pos - 2))));
                        pos = end + 2;
                    }
                    else
                    {
                        sb.Append("**");
                        pos += 2;
                    }
                    continue;
                }

                // *italic* (single *, not part of **)
                if (c == '*')
                {
                    FlushPlain(inlines, sb);
                    int end = FindSingleAsterisk(text, pos + 1);
                    if (end >= 0)
                    {
                        inlines.Add(new Italic(new Run(text.Substring(pos + 1, end - pos - 1))));
                        pos = end + 1;
                    }
                    else
                    {
                        sb.Append('*');
                        pos++;
                    }
                    continue;
                }

                // `inline code`
                if (c == '`')
                {
                    FlushPlain(inlines, sb);
                    int end = text.IndexOf('`', pos + 1);
                    if (end >= 0)
                    {
                        inlines.Add(new Run(text.Substring(pos + 1, end - pos - 1))
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xCE, 0x91, 0x78))
                        });
                        pos = end + 1;
                    }
                    else
                    {
                        sb.Append('`');
                        pos++;
                    }
                    continue;
                }

                sb.Append(c);
                pos++;
            }

            FlushPlain(inlines, sb);
        }

        private static void FlushPlain(InlineCollection inlines, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            inlines.Add(new Run(sb.ToString()));
            sb.Clear();
        }

        /// <summary>
        /// Returns the index of a <c>*</c> character that is not part of <c>**</c>,
        /// searching from <paramref name="start"/>. Returns -1 if none found.
        /// </summary>
        private static int FindSingleAsterisk(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] != '*') continue;
                if (i + 1 < text.Length && text[i + 1] == '*') { i++; continue; }
                return i;
            }
            return -1;
        }
    }
}
