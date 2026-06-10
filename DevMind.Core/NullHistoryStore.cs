// File: NullHistoryStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// No-op implementation of IHistoryStore. Used when history is disabled
// or no provider is configured.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>
    /// No-op implementation of IHistoryStore. Used when history is disabled
    /// or no provider is configured.
    /// </summary>
    public sealed class NullHistoryStore : IHistoryStore
    {
        public Task SaveMessagesAsync(HistoryMessage[] messages) => Task.CompletedTask;
        public Task<HistoryMessage[]> LoadMessagesAsync(string machineName, int maxTurns) => Task.FromResult(Array.Empty<HistoryMessage>());
        public Task<SessionSummary[]> ListSessionsAsync(string machineName) => Task.FromResult(Array.Empty<SessionSummary>());
        public Task UpsertSessionAsync(string sessionId, string machineName) => Task.CompletedTask;
        public Task SetSessionTitleAsync(string sessionId, string title) => Task.CompletedTask;
        public Task<HistoryMessage[]> LoadSessionMessagesAsync(string sessionId) => Task.FromResult(Array.Empty<HistoryMessage>());
        public Task InitAsync() => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
    }
}
