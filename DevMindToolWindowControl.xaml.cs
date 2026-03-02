// File: DevMindToolWindowControl.xaml.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevMind
{
    /// <summary>
    /// WPF control for the DevMind chat tool window.
    /// Displays a scrollable message history, text input, and status bar.
    /// </summary>
    public partial class DevMindToolWindowControl : UserControl
    {
        private readonly LlmClient _llmClient;
        private readonly ObservableCollection<ChatMessageViewModel> _messages;
        private CancellationTokenSource _cts;

        /// <summary>
        /// Initializes a new instance of the <see cref="DevMindToolWindowControl"/> class.
        /// </summary>
        /// <param name="llmClient">The LLM client used to communicate with the AI endpoint.</param>
        public DevMindToolWindowControl(LlmClient llmClient)
        {
            InitializeComponent();
            Themes.SetUseVsTheme(this, true);
            _llmClient = llmClient;
            _messages = new ObservableCollection<ChatMessageViewModel>();
            MessagesPanel.ItemsSource = _messages;

            DevMindOptions.Saved += OnSettingsSaved;
        }

        /// <summary>
        /// Updates the status bar text.
        /// </summary>
        /// <param name="status">The status text to display.</param>
        public void SetStatus(string status)
        {
            StatusText.Text = status;
        }

        private void OnSettingsSaved(DevMindOptions options)
        {
            _llmClient.Configure(options.EndpointUrl, options.ApiKey);
            TestConnectionInBackground();
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
                // Allow Shift+Enter to insert a newline
                int caretIndex = InputTextBox.CaretIndex;
                InputTextBox.Text = InputTextBox.Text.Insert(caretIndex, Environment.NewLine);
                InputTextBox.CaretIndex = caretIndex + Environment.NewLine.Length;
                e.Handled = true;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

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

            // Add user message (right-aligned, blue bubble)
            _messages.Add(ChatMessageViewModel.UserMessage(text));
            ScrollToBottom();

            // Add placeholder for AI response (left-aligned, gray bubble)
            var aiMessage = ChatMessageViewModel.AssistantMessage("");
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
                            aiMessage.Text += token;
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
                            aiMessage.Text += $"\n[Error: {ex.Message}]";
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

        private void ScrollToBottom()
        {
            ChatScrollViewer.ScrollToEnd();
        }

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

    /// <summary>
    /// View model for a single chat message displayed in the message history.
    /// </summary>
    public class ChatMessageViewModel : INotifyPropertyChanged
    {
        private string _text;

        /// <summary>The message text content.</summary>
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        /// <summary>Horizontal alignment (Right for user, Left for assistant).</summary>
        public HorizontalAlignment Alignment { get; set; }

        /// <summary>Background brush for the message bubble.</summary>
        public Brush Background { get; set; }

        /// <summary>Foreground brush for the message text.</summary>
        public Brush Foreground { get; set; }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Creates a view model for a user message (right-aligned, blue bubble).
        /// </summary>
        /// <param name="text">The message text.</param>
        public static ChatMessageViewModel UserMessage(string text)
        {
            return new ChatMessageViewModel
            {
                Text = text,
                Alignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White
            };
        }

        /// <summary>
        /// Creates a view model for an assistant message (left-aligned, gray bubble).
        /// </summary>
        /// <param name="text">The message text.</param>
        public static ChatMessageViewModel AssistantMessage(string text)
        {
            return new ChatMessageViewModel
            {
                Text = text,
                Alignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };
        }
    }
}
