// File: SqlServerHistoryStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// SQL Server-backed implementation of IHistoryStore using Microsoft.Data.SqlClient.
// Stores conversation history in DevMindHistory and DevMindSessions tables.

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace DevMind
{
    /// <summary>
    /// SQL Server implementation of IHistoryStore. Creates tables on init if they don't exist.
    /// </summary>
    public sealed class SqlServerHistoryStore : IHistoryStore
    {
        private SqlConnection _connection;

        public SqlServerHistoryStore(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        private readonly string _connectionString;

        async Task EnsureConnectionAsync()
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                _connection = new SqlConnection(_connectionString);
                await _connection.OpenAsync();
            }
        }

        public async Task InitAsync()
        {
            await EnsureConnectionAsync();

            // Create DevMindHistory table
            using (var cmd = new SqlCommand(@"
                IF OBJECT_ID(N'dbo.DevMindHistory', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.DevMindHistory (
                        Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
                        SessionId   VARCHAR(64) NOT NULL,
                        MachineName VARCHAR(128) NOT NULL,
                        TurnIndex   INT NOT NULL,
                        Role        VARCHAR(16) NOT NULL,
                        Content     NVARCHAR(MAX) NOT NULL,
                        CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                END", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Create index
            using (var cmd = new SqlCommand(@"
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_DevMindHistory_Machine_Created'
                      AND object_id = OBJECT_ID(N'dbo.DevMindHistory')
                )
                BEGIN
                    CREATE INDEX IX_DevMindHistory_Machine_Created
                      ON dbo.DevMindHistory(MachineName, CreatedAt DESC);
                END", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Create DevMindSessions table
            using (var cmd = new SqlCommand(@"
                IF OBJECT_ID(N'dbo.DevMindSessions', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.DevMindSessions (
                        SessionId    VARCHAR(64)   NOT NULL PRIMARY KEY,
                        MachineName  VARCHAR(128)  NOT NULL,
                        Title        NVARCHAR(255) NULL,
                        StartedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
                        LastActiveAt DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                END", _connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task SaveMessagesAsync(HistoryMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;

            await EnsureConnectionAsync();

           SqlTransaction tx = _connection.BeginTransaction();
            try
            {
                foreach (var m in messages)
                {
                    using var cmd = new SqlCommand(@"
                        INSERT INTO dbo.DevMindHistory (SessionId, MachineName, TurnIndex, Role, Content, CreatedAt)
                        VALUES (@sessionId, @machineName, @turnIndex, @role, @content, @createdAt)", _connection, tx);
                    cmd.Parameters.Add("@sessionId", SqlDbType.VarChar, 64).Value = m.SessionId;
                    cmd.Parameters.Add("@machineName", SqlDbType.VarChar, 128).Value = m.MachineName;
                    cmd.Parameters.Add("@turnIndex", SqlDbType.Int).Value = m.TurnIndex;
                    cmd.Parameters.Add("@role", SqlDbType.VarChar, 16).Value = m.Role;
                    cmd.Parameters.Add("@content", SqlDbType.NVarChar, -1).Value = (object)m.Content ?? DBNull.Value;
                    cmd.Parameters.Add("@createdAt", SqlDbType.DateTime2).Value = m.CreatedAt;
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

            int limit = maxTurns * 2; // user + assistant pairs
            var list = new List<HistoryMessage>();

            using var cmd = new SqlCommand(@"
                SELECT TOP (@limit) SessionId, MachineName, TurnIndex, Role, Content, CreatedAt
                FROM dbo.DevMindHistory
                WHERE MachineName = @machine
                ORDER BY CreatedAt DESC", _connection);
            cmd.Parameters.Add("@machine", SqlDbType.VarChar).Value = machineName;
            cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;

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

            using var cmd = new SqlCommand(@"
                SELECT
                    s.SessionId,
                    s.Title,
                    s.StartedAt,
                    s.LastActiveAt,
                    COUNT(h.Id) AS MessageCount
                FROM dbo.DevMindSessions s
                LEFT JOIN dbo.DevMindHistory h ON h.SessionId = s.SessionId
                WHERE s.MachineName = @machine
                GROUP BY s.SessionId, s.Title, s.StartedAt, s.LastActiveAt
                ORDER BY s.LastActiveAt DESC", _connection);
            cmd.Parameters.Add("@machine", SqlDbType.VarChar).Value = machineName;

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

            using var cmd = new SqlCommand(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.DevMindSessions WHERE SessionId = @sessionId)
                BEGIN
                    INSERT INTO dbo.DevMindSessions (SessionId, MachineName)
                    VALUES (@sessionId, @machineName);
                END
                ELSE
                BEGIN
                    UPDATE dbo.DevMindSessions
                    SET LastActiveAt = SYSUTCDATETIME()
                    WHERE SessionId = @sessionId;
                END", _connection);
            cmd.Parameters.Add("@sessionId", SqlDbType.VarChar, 64).Value = sessionId;
            cmd.Parameters.Add("@machineName", SqlDbType.VarChar, 128).Value = machineName;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetSessionTitleAsync(string sessionId, string title)
        {
            await EnsureConnectionAsync();

            using var cmd = new SqlCommand(@"
                UPDATE dbo.DevMindSessions
                SET Title = @title
                WHERE SessionId = @sessionId", _connection);
            cmd.Parameters.Add("@sessionId", SqlDbType.VarChar, 64).Value = sessionId;
            cmd.Parameters.Add("@title", SqlDbType.NVarChar, 255).Value = (object)title ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId)
        {
            await EnsureConnectionAsync();

            var list = new List<HistoryMessage>();

            using var cmd = new SqlCommand(@"
                SELECT SessionId, MachineName, TurnIndex, Role, Content, CreatedAt
                FROM dbo.DevMindHistory
                WHERE SessionId = @sessionId
                ORDER BY TurnIndex ASC, CreatedAt ASC", _connection);
            cmd.Parameters.Add("@sessionId", SqlDbType.VarChar, 64).Value = sessionId;

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
