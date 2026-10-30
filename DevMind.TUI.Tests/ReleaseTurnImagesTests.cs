// File: ReleaseTurnImagesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Program.ReleaseTurnImages — the end-of-turn image release RunTurnAsync's finally runs.
// Drives a real LlmClient over the fake SSE server: an image sent in one turn is gone from
// the next request after release (and the echo line reports the count); with the setting
// off, or with nothing to release, it returns null and changes nothing.

using DevMind.Core.Tests;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class ReleaseTurnImagesTests : IDisposable
    {
        private readonly FakeSseServer _server = new FakeSseServer();
        private readonly LlmClient _client;

        public ReleaseTurnImagesTests()
        {
            _client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768, // skip context probes
            });
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { _client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
        }

        public void Dispose() => _server.Dispose();

        private async Task SendAsync(string message)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.IncrementTurn();
            await _client.SendMessageAsync(message,
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        [Fact]
        public async Task Enabled_ReleasesImages_EchoesCount_AndNextRequestHasNoImage()
        {
            _client.StagePendingImage("data:image/png;base64,ONE", @"C:\imgs\001-one.png");
            _client.StagePendingImage("data:image/png;base64,TWO", null);
            await SendAsync("look");

            Assert.Equal("[images released from context: 2]\n", Program.ReleaseTurnImages(_client, enabled: true));
            Assert.Null(Program.ReleaseTurnImages(_client, enabled: true)); // nothing left

            await SendAsync("again");
            string body = _server.RequestBodies[1];
            Assert.DoesNotContain("image_url", body, StringComparison.Ordinal);
            Assert.Contains("001-one.png", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Disabled_KeepsImagesInContext()
        {
            _client.StagePendingImage("data:image/png;base64,ONE", @"C:\imgs\001-one.png");
            await SendAsync("look");

            Assert.Null(Program.ReleaseTurnImages(_client, enabled: false));

            await SendAsync("again");
            Assert.Contains("image_url", _server.RequestBodies[1], StringComparison.Ordinal);
        }
    }
}
