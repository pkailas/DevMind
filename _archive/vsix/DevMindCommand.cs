// File: DevMindCommand.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DevMind
{
    /// <summary>
    /// Command handler that shows the DevMind tool window.
    /// Registered under View > Other Windows > DevMind with shortcut Ctrl+Alt+D.
    /// </summary>
    [Command(PackageGuids.DevMindPackageGuidString, PackageIds.ShowToolWindow)]
    internal sealed class DevMindCommand : BaseCommand<DevMindCommand>
    {
        /// <inheritdoc/>
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await DevMindToolWindow.ShowAsync();
        }
    }
}
