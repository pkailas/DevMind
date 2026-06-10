// File: IHistoryStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Core types and interface for conversation history persistence.
// Defines the contract that history store implementations must satisfy.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>A single message in a conversation turn.</summary>
    public sealed class HistoryMessage
    {
        public string SessionId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public int TurnIndex { get; set; }
        public string Role { get; set; } = ""; // "user" or "assistant"
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Summary of a single session returned by ListSessions.</summary>
    public sealed class SessionSummary
    {
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime LastActiveAt { get; set; }
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// Contract for persisting and loading conversation history.
    /// Implementations: SqlServerHistoryStore, SqliteHistoryStore, NullHistoryStore.
    /// </summary>
    public interface IHistoryStore
    {
        /// <summary>Persist a batch of messages to the backing store.</summary>
        Task SaveMessagesAsync(HistoryMessage[] messages);

        /// <summary>Load the most recent messages for a given machine, up to maxTurns turns.</summary>
        Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns);

        /// <summary>List all sessions for a given machine.</summary>
        Task<SessionSummary[]> ListSessionsAsync(string machineName);

        /// <summary>Upsert a session record (insert if new, update LastActiveAt if existing).</summary>
        Task UpsertSessionAsync(string sessionId, string machineName);

        /// <summary>Set or update the title of a session.</summary>
        Task SetSessionTitleAsync(string sessionId, string title);

        /// <summary>Load all messages for a specific session, in chronological order.</summary>
        Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId);

        /// <summary>One-time initialization (e.g., schema migration, connection setup).</summary>
        Task InitAsync();

        /// <summary>Release resources (e.g., close database connections).</summary>
        Task CloseAsync();
    }
}
