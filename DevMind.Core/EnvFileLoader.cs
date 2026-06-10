// File: EnvFileLoader.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Reads ~/.devmind.env at startup and applies KEY=value pairs that are not
// already set in the process environment. Existing env vars always win.

using System;
using System.IO;

namespace DevMind
{
    /// <summary>
    /// Loads environment variables from ~/.devmind.env at startup.
    /// </summary>
    public static class EnvFileLoader
    {
        /// <summary>
        /// Reads the env file and applies any KEY=value pairs not already set.
        /// Returns the number of variables loaded.
        /// </summary>
        public static int Load()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".devmind.env");

            if (!File.Exists(path))
            {
                Console.Error.WriteLine("[env] ~/.devmind.env not found, skipping");
                return 0;
            }

            int count = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                // Skip blank lines and comments.
                if (line.Length == 0 || line[0] == '#')
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue; // malformed — no key or no '='

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (string.IsNullOrEmpty(key))
                    continue;

                // Only set if not already present.
                if (Environment.GetEnvironmentVariable(key) == null)
                {
                    Environment.SetEnvironmentVariable(key, value);
                    count++;
                }
            }

            Console.Error.WriteLine($"[env] Loaded {count} vars from ~/.devmind.env");
            return count;
        }
    }
}
