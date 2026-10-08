// File: StrataServerTypeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// LlmServerType.Strata: identified, not yet treated differently. Strata answers /props like
// llama-server and stamps build_info "Strata <version>"; before it had a type of its own it ran
// as LlamaServer only because nothing else claimed it. Detection order is the DEVMIND_SERVER_TYPE
// override, then /v1/models (vLLM), then /props (Strata). Strata takes every LlamaServer branch
// for now, so the request on the wire must be identical.

using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class StrataServerTypeTests : IDisposable
    {
        private const string EnvVar = "DEVMIND_SERVER_TYPE";
        private readonly string? _priorServerType = Environment.GetEnvironmentVariable(EnvVar);
        private readonly FakeSseServer _server = new FakeSseServer();

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvVar, _priorServerType);
            _server.Dispose();
        }

        private static string Props(string? buildInfo, int nCtx = 65536)
        {
            var props = new JObject
            {
                ["default_generation_settings"] = new JObject { ["n_ctx"] = nCtx, ["params"] = new JObject() },
                ["total_slots"] = 1,
                ["model_alias"] = "Strata-Flash-Next",
            };
            if (buildInfo != null) props["build_info"] = buildInfo;
            return props.ToString();
        }

        private const string VllmModels =
            "{\"object\":\"list\",\"data\":[{\"id\":\"qwen\",\"object\":\"model\",\"owned_by\":\"vllm\",\"max_model_len\":40960}]}";

        /// <summary>
        /// Configures a client against the fake server with <see cref="EnvVar"/> set to
        /// <paramref name="forced"/> (null = auto-detect), and waits for the detection
        /// Configure() starts.
        /// </summary>
        private async Task<LlmClient> ConfigureAndDetectAsync(string? forced, int manualContextSize = 0)
        {
            var client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = manualContextSize,
            });
            Environment.SetEnvironmentVariable(EnvVar, forced);
            try { client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable(EnvVar, _priorServerType); }

            var task = (Task?)typeof(LlmClient)
                .GetField("_contextDetectionTask", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(client);
            Assert.NotNull(task);
            await task!.WaitAsync(TimeSpan.FromSeconds(15));
            return client;
        }

        private string[] Gets() { lock (_server.GetPaths) return _server.GetPaths.ToArray(); }

        [Fact]
        public async Task StrataBuildInfo_InProps_AndNoVllmModels_DetectsStrata()
        {
            _server.GetResponses["/props"] = (200, Props("Strata 0.9.1"));

            LlmClient client = await ConfigureAndDetectAsync(forced: null);

            Assert.Equal(LlmServerType.Strata, client.ServerType);
        }

        [Theory]
        [InlineData(null)]          // llama-server builds without the field
        [InlineData("b10499-abc")]  // llama.cpp's own build_info
        [InlineData("")]
        public async Task PropsWithoutAStrataBuildInfo_StaysLlamaServer(string? buildInfo)
        {
            _server.GetResponses["/props"] = (200, Props(buildInfo));

            LlmClient client = await ConfigureAndDetectAsync(forced: null);

            Assert.Equal(LlmServerType.LlamaServer, client.ServerType);
        }

        [Fact]
        public async Task PropsProbeFailure_LeavesTheConfiguredType()
        {
            _server.GetResponses["/props"] = (500, "{\"error\":\"engine down\"}");

            LlmClient client = await ConfigureAndDetectAsync(forced: null);

            Assert.Equal(LlmServerType.LlamaServer, client.ServerType);
        }

        [Fact]
        public async Task VllmModels_WinOverAStrataProps()
        {
            _server.GetResponses["/v1/models"] = (200, VllmModels);
            _server.GetResponses["/props"] = (200, Props("Strata 0.9.1"));

            LlmClient client = await ConfigureAndDetectAsync(forced: null);

            Assert.Equal(LlmServerType.Vllm, client.ServerType);
        }

        [Fact]
        public async Task EnvOverrideStrata_SetsStrata_WithoutProbing()
        {
            // Manual context size: no context probe either, so any GET would be a type probe.
            LlmClient client = await ConfigureAndDetectAsync(forced: "strata", manualContextSize: 32768);

            Assert.Equal(LlmServerType.Strata, client.ServerType);
            Assert.Empty(Gets());
            Assert.Equal(_priorServerType, Environment.GetEnvironmentVariable(EnvVar));
        }

        [Fact]
        public async Task ContextDetection_UnderStrata_ReadsNCtxFromProps_InOneRoundTrip()
        {
            _server.GetResponses["/props"] = (200, Props("Strata 0.9.1", nCtx: 131072));

            LlmClient client = await ConfigureAndDetectAsync(forced: null);

            Assert.Equal(LlmServerType.Strata, client.ServerType);
            Assert.Equal(131072, client.ServerContextSize);
            Assert.Single(Gets(), p => p == "/props"); // the type probe's body was reused
        }

        [Fact]
        public async Task ContextDetection_UnderForcedStrata_FetchesProps()
        {
            // No probe ran, so nothing to reuse: context detection fetches /props itself.
            _server.GetResponses["/props"] = (200, Props("Strata 0.9.1", nCtx: 98304));

            LlmClient client = await ConfigureAndDetectAsync(forced: "strata");

            Assert.Equal(98304, client.ServerContextSize);
            Assert.Equal(new[] { "/props" }, Gets());
        }

        private async Task<JObject> SendOneAndGetBodyAsync(string forced)
        {
            var client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                ShowLlmThinking = true,
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768,
            });
            Environment.SetEnvironmentVariable(EnvVar, forced);
            try { client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable(EnvVar, _priorServerType); }

            int before = _server.ChatPostCount;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync("hello",
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(before + 1, _server.ChatPostCount);
            lock (_server.RequestBodies) return JObject.Parse(_server.RequestBodies.Last());
        }

        [Fact]
        public async Task ARequestUnderStrata_IsTheSameAsUnderLlamaServer()
        {
            JObject llama = await SendOneAndGetBodyAsync("llama");
            JObject strata = await SendOneAndGetBodyAsync("strata");

            Assert.True(JToken.DeepEquals(llama, strata),
                "request bodies differ:\nllama:  " + llama.ToString(Newtonsoft.Json.Formatting.None)
                + "\nstrata: " + strata.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True((bool)strata["timings_per_token"]!);
            Assert.NotNull(strata["chat_template_kwargs"]?["enable_thinking"]);
        }

        [Theory]
        [InlineData(LlmServerType.LlamaServer, true)]
        [InlineData(LlmServerType.Strata, true)]
        [InlineData(LlmServerType.Vllm, false)]
        [InlineData(LlmServerType.LmStudio, false)]
        [InlineData(LlmServerType.Custom, false)]
        public void SpeaksLlamaServerProtocol_IsLlamaServerAndStrata(LlmServerType type, bool expected)
            => Assert.Equal(expected, type.SpeaksLlamaServerProtocol());

        [Fact]
        public void Strata_IsAppended_SoNoExistingValueMoves()
        {
            Assert.Equal(0, (int)LlmServerType.LlamaServer);
            Assert.Equal(1, (int)LlmServerType.LmStudio);
            Assert.Equal(2, (int)LlmServerType.Vllm);
            Assert.Equal(3, (int)LlmServerType.Custom);
            Assert.Equal(4, (int)LlmServerType.Strata);
        }
    }
}
