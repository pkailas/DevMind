// File: DevMindToolWindow.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DevMind
{
    /// <summary>
    /// The DevMind chat tool window, providing a local LLM coding assistant interface.
    /// </summary>
    public class DevMindToolWindow : BaseToolWindow<DevMindToolWindow>
    {
        /// <inheritdoc/>
        public override string GetTitle(int toolWindowId) => "DevMind";

        /// <inheritdoc/>
        public override Type PaneType => typeof(Pane);

        /// <inheritdoc/>
        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            var settings = await DevMindOptions.GetLiveInstanceAsync();
            var llmClient = new LlmClient();
            llmClient.Configure(settings.EndpointUrl, settings.ApiKey);

            var control = new DevMindToolWindowControl(llmClient);

            // Test connection on startup and update status
            bool connected = await llmClient.TestConnectionAsync();
            control.SetStatus(connected ? "Connected" : "Disconnected");

            return control;
        }

        /// <summary>
        /// The tool window pane that hosts the DevMind chat control.
        /// </summary>
        [Guid("b8f3c4d5-e6a7-4890-bcde-f12345678901")]
        internal class Pane : ToolWindowPane
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="Pane"/> class.
            /// </summary>
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.StatusInformation;
            }
        }
    }
}
