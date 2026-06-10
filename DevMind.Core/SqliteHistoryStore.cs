// File: SqliteHistoryStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// SQLite-backed implementation of IHistoryStore using Microsoft.Data.Sqlite.
// Stores conversation history in DevMindHistory and DevMindSessions tables.

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DevMind
{
    /// <summary>
    /// SQLite implementation of IHistoryStore. Creates tables on init if they don't exist.
    /// </summary>
    public sealed class SqliteHistoryStore : IHistoryStore
    {
        private SqliteConnection _connection;

        public SqliteHistoryStore(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        private readonly string _dbPath;

        async Task EnsureConnectionAsync()
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                await _connection.OpenAsync();
            }
        }

        public async Task InitAsync()
        {
            await EnsureConnectionAsync();

            using (var cmd = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS DevMindHistory (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId   TEXT NOT NULL,
                    MachineName TEXT NOT NULL,
                    TurnIndex   INTEGER NOT NULL,
                    Role        TEXT NOT NULL,
                    Content     TEXT NOT NULL,
                    CreatedAt   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new SqliteCommand(@"
                CREATE INDEX IF NOT EXISTS IX_DevMindHistory_Machine_Created
                  ON DevMindHistory(MachineName, CreatedAt DESC);", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS DevMindSessions (
                    SessionId    TEXT NOT NULL PRIMARY KEY,
                    MachineName  TEXT NOT NULL,
                    Title        TEXT,
                    StartedAt    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    LastActiveAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task SaveMessagesAsync(HistoryMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;

            await EnsureConnectionAsync();

           SqliteTransaction tx = (SqliteTransaction)_connection.BeginTransaction();
            try
            {
                foreach (var m in messages)
                {
                    using var cmd = new SqliteCommand(@"
                        INSERT INTO DevMindHistory (SessionId, MachineName, TurnIndex, Role, Content, CreatedAt)
                        VALUES (@sessionId, @machineName, @turnIndex, @role, @content, @createdAt)", _connection, tx);
                    cmd.Parameters.AddWithValue("@sessionId", m.SessionId);
                    cmd.Parameters.AddWithValue("@machineName", m.MachineName);
                    cmd.Parameters.AddWithValue("@turnIndex", m.TurnIndex);
                    cmd.Parameters.AddWithValue("@role", m.Role);
                    cmd.Parameters.AddWithValue("@content", (object)m.Content ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@createdAt", m.CreatedAt);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns)
        {
            await EnsureConnectionAsync();

            int limit = maxTurns * 2;
            var list = new List<HistoryMessage>();

            using var cmd = new SqliteCommand(@"
                SELECT SessionId, MachineName, TurnIndex, Role, Content, CreatedAt
                FROM DevMindHistory
                WHERE MachineName = @machine
                ORDER BY CreatedAt DESC
                LIMIT @limit", _connection);
            cmd.Parameters.AddWithValue("@machine", machineName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new HistoryMessage
                {
                    SessionId = reader.GetString(0),
                    MachineName = reader.GetString(1),
                    TurnIndex = reader.GetInt32(2),
                    Role = reader.GetString(3),
                    Content = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                });
            }

            list.Reverse();
            return list.ToArray();
        }

        public async Task<SessionSummary[]> ListSessionsAsync(string machineName)
        {
            await EnsureConnectionAsync();

            var list = new List<SessionSummary>();

            using var cmd = new SqliteCommand(@"
                SELECT
                    s.SessionId,
                    s.Title,
                    s.StartedAt,
                    s.LastActiveAt,
                    COUNT(h.Id) AS MessageCount
                FROM DevMindSessions s
                LEFT JOIN DevMindHistory h ON h.SessionId = s.SessionId
                WHERE s.MachineName = @machine
                GROUP BY s.SessionId, s.Title, s.StartedAt, s.LastActiveAt
                ORDER BY s.LastActiveAt DESC", _connection);
            cmd.Parameters.AddWithValue("@machine", machineName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new SessionSummary
                {
                    SessionId = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    StartedAt = reader.GetDateTime(2),
                    LastActiveAt = reader.GetDateTime(3),
                    MessageCount = reader.GetInt32(4),
                });
            }

            return list.ToArray();
        }

        public async Task UpsertSessionAsync(string sessionId, string machineName)
        {
            await EnsureConnectionAsync();

            using var cmd = new SqliteCommand(@"
                INSERT INTO DevMindSessions (SessionId, MachineName)
                VALUES (@sessionId, @machineName)
                ON CONFLICT(SessionId) DO UPDATE SET LastActiveAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')", _connection);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@machineName", machineName);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetSessionTitleAsync(string sessionId, string title)
        {
            await EnsureConnectionAsync();

            using var cmd = new SqliteCommand(@"
                UPDATE DevMindSessions
                SET Title = @title
                WHERE SessionId = @sessionId", _connection);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@title", (object)title ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId)
        {
            await EnsureConnectionAsync();

            var list = new List<HistoryMessage>();

            using var cmd = new SqliteCommand(@"
                SELECT SessionId, MachineName, TurnIndex, Role, Content, CreatedAt
                FROM DevMindHistory
                WHERE SessionId = @sessionId
                ORDER BY TurnIndex ASC, CreatedAt ASC", _connection);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new HistoryMessage
                {
                    SessionId = reader.GetString(0),
                    MachineName = reader.GetString(1),
                    TurnIndex = reader.GetInt32(2),
                    Role = reader.GetString(3),
                    Content = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                });
            }

            return list.ToArray();
        }

        public async Task CloseAsync()
        {
            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
                _connection = null;
            }
        }
    }
}
