// File: NpmGlobalToolResolver.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Resolves npm global CLI tools to an executable path. On Windows, npm installs
// .cmd shims under %APPDATA%\npm; ProcessStartInfo with UseShellExecute=false
// does not resolve bare command names to those shims.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DevMind
{
    public static class NpmGlobalToolResolver
    {
        /// <summary>
        /// Returns a path suitable for ProcessStartInfo.FileName, or null if not found.
        /// </summary>
        public static string Resolve(string toolBaseName)
        {
            if (string.IsNullOrWhiteSpace(toolBaseName))
                return null;

            toolBaseName = toolBaseName.Trim();

            if (Path.IsPathRooted(toolBaseName) && File.Exists(toolBaseName))
                return Path.GetFullPath(toolBaseName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ResolveWindows(toolBaseName);

            return ResolveUnix(toolBaseName);
        }

        private static string ResolveWindows(string toolBaseName)
        {
            string cmdName = toolBaseName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? toolBaseName
                : toolBaseName + ".cmd";

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string shim = Path.Combine(appData, "npm", cmdName);
                if (File.Exists(shim))
                    return Path.GetFullPath(shim);
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string shim = Path.Combine(localAppData, "npm", cmdName);
                if (File.Exists(shim))
                    return Path.GetFullPath(shim);
            }

            return TryWhere(toolBaseName) ?? TryWhere(cmdName);
        }

        private static string ResolveUnix(string toolBaseName)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                string[] candidates =
                {
                    Path.Combine(home, ".npm-global", "bin", toolBaseName),
                    Path.Combine(home, ".local", "bin", toolBaseName),
                    Path.Combine("/usr", "local", "bin", toolBaseName),
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }

            return TryWhich(toolBaseName);
        }

        private static string TryWhere(string name)
        {
            try
            {
                string whereExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "where.exe");
                if (!File.Exists(whereExe))
                    return null;

                var psi = new ProcessStartInfo(whereExe, "\"" + name + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return null;

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(2000);
                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                        return null;

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string path = line.Trim();
                        if (File.Exists(path))
                            return Path.GetFullPath(path);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string TryWhich(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/which", name)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return null;

                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(2000);
                    if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
                        return null;

                    return File.Exists(output) ? Path.GetFullPath(output) : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
