// File: HistoryStoreFactory.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Factory for creating IHistoryStore instances based on runtime config.
// Reads DEVMIND_HISTORY_* environment variables.
// The caller is responsible for calling InitAsync() and CloseAsync() on the returned store.

using System;

namespace DevMind
{
    /// <summary>
    /// Factory for creating IHistoryStore instances based on environment variables.
    /// </summary>
    public static class HistoryStoreFactory
    {
        /// <summary>
        /// Creates an IHistoryStore based on DEVMIND_HISTORY_* environment variables.
        /// Falls back to NullHistoryStore when history is disabled or misconfigured.
        /// </summary>
        public static IHistoryStore Create()
        {
            bool enabled = ParseBooleanEnv(Environment.GetEnvironmentVariable("DEVMIND_HISTORY_ENABLED"));
            if (!enabled)
                return new NullHistoryStore();

            string provider = Environment.GetEnvironmentVariable("DEVMIND_HISTORY_PROVIDER")?.Trim().ToLowerInvariant() ?? "none";

           if (provider == "sqlserver")
                return CreateSqlServerStore();
            if (provider == "sqlite")
                return CreateSqliteStore();
            Console.Error.WriteLine($"[history] Unknown provider \"{provider}\"; falling back to NullHistoryStore.");
            return new NullHistoryStore();
        }

        static IHistoryStore CreateSqlServerStore()
        {
            string connStr = Environment.GetEnvironmentVariable("DEVMIND_HISTORY_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connStr))
            {
                Console.Error.WriteLine("[history] DEVMIND_HISTORY_CONNECTION_STRING not set; falling back to NullHistoryStore.");
                return new NullHistoryStore();
            }
            return new SqlServerHistoryStore(connStr);
        }

        static IHistoryStore CreateSqliteStore()
        {
            string dbPath = Environment.GetEnvironmentVariable("DEVMIND_HISTORY_DB_PATH");
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                Console.Error.WriteLine("[history] DEVMIND_HISTORY_DB_PATH not set; falling back to NullHistoryStore.");
                return new NullHistoryStore();
            }
            return new SqliteHistoryStore(dbPath);
        }

       static bool ParseBooleanEnv(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            return v == "true" || v == "yes" || v == "1";
        }
    }
}
