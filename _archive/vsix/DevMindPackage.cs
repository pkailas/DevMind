// File: DevMindPackage.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace DevMind
{
    /// <summary>
    /// The main package class for the DevMind extension. Registers the tool window,
    /// options page, and menu commands with the Visual Studio shell.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("DevMind", "Local LLM coding assistant for Visual Studio", "2.2.0")]
    [ProvideToolWindow(typeof(DevMindToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
    [ProvideOptionPage(typeof(OptionsProvider.DevMindOptionsPage), "DevMind", "General", 0, 0, true, SupportsProfiles = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.DevMindPackageGuidString)]
    public sealed class DevMindPackage : ToolkitPackage
    {
        /// <summary>
        /// Initializes the package asynchronously. Registers commands and tool windows
        /// with the Visual Studio shell.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation.</param>
        /// <param name="progress">A provider for progress updates.</param>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}
