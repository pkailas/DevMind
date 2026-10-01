// File: ReasoningEffortJobParameterTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// devmind_task_start reasoning_effort (low|medium|high|xhigh):
//   * an unknown value rejects the start, listing the allowed values; no job is queued
//   * supplying it implies think: true (same rule as show_thinking)
//   * think on with nothing supplied -> "medium", or devmind.json "reasoningEffort" when set;
//     a per-job value wins over devmind.json
//   * think off -> reasoning_effort null in the start response and the result, and the
//     request carries no reasoning_effort
//   * continuations inherit it
// Every test points DEVMIND_GLOBAL_DIR at a private temp dir so the operator's real
// devmind.json can never decide the outcome.

using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class ReasoningEffortJobParameterTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _globalDir;
        private readonly string? _priorEndpoint;
        private readonly string? _priorServerType;
        private readonly string? _priorGlobalDir;

        public ReasoningEffortJobParameterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_effort_job_{Guid.NewGuid():N}");
            _globalDir = Path.Combine(_dir, "global");
            Directory.CreateDirectory(_globalDir);
            _priorEndpoint = Environment.GetEnvironmentVariable("DEVMIND_ENDPOINT");
            _priorServerType = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", _priorEndpoint);
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", _priorServerType);
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private void WriteGlobalConfig(string json)
            => File.WriteAllText(Path.Combine(_globalDir, "devmind.json"), json);

        private static async Task<AgentJob> WaitForDone(AgentJob job, int timeoutMs = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs
                   && job.State is AgentJobState.Queued or AgentJobState.Running)
                await Task.Delay(50);
            Assert.Equal(AgentJobState.Done, job.State);
            return job;
        }

        /// <summary>chat_template_kwargs of every chat request the stub received.</summary>
        private static List<JObject> TemplateKwargs(ThinkTokenLlmServer server)
        {
            lock (server.ChatBodies)
                return server.ChatBodies
                    .Select(b => JObject.Parse(b))
                    .Where(b => b["messages"] != null)
                    .Select(b => (JObject)b["chat_template_kwargs"]!)
                    .ToList();
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        // ── validation ───────────────────────────────────────────────────────

        [Fact]
        public async Task TaskStart_UnknownReasoningEffort_IsRejected_WithAllowedList()
        {
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            string json = await tools.TaskStart("p", _dir, reasoning_effort: "turbo");

            Assert.DoesNotContain("job_id", json);
            Assert.Contains("low, medium, high, xhigh", json);
            Assert.Contains("turbo", json);
            Assert.Empty(mgr.List());
        }

        // ── implies think; the start response and the wire carry the value ──

        [Fact]
        public async Task TaskStart_ReasoningEffortWithThinkOmitted_TurnsThinkingOn()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            string json = await tools.TaskStart("p", _dir, think: null, reasoning_effort: "LOW");
            Assert.Equal("low", Parse(json).GetProperty("reasoning_effort").GetString());

            var job = mgr.List().Single();
            Assert.True(job.Think);
            Assert.Equal("low", job.ReasoningEffort);

            await WaitForDone(job);
            var ctk = TemplateKwargs(server).First();
            Assert.True((bool)ctk["enable_thinking"]!);
            Assert.Equal("low", (string?)ctk["reasoning_effort"]);

            string result = await tools.TaskResult(job.Id);
            Assert.Equal("low", Parse(result).GetProperty("reasoning_effort").GetString());
        }

        [Fact]
        public async Task TaskStart_ThinkOn_NoEffortAnywhere_DefaultsToMedium()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            string json = await tools.TaskStart("p", _dir, think: true);
            Assert.Equal("medium", Parse(json).GetProperty("reasoning_effort").GetString());

            var job = await WaitForDone(mgr.List().Single());
            Assert.Equal("medium", (string?)TemplateKwargs(server).First()["reasoning_effort"]);
        }

        [Fact]
        public async Task TaskStart_ThinkOff_EchoesNull_AndSendsNoEffort()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            WriteGlobalConfig("{ \"reasoningEffort\": \"xhigh\" }"); // must not switch thinking on
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            string json = await tools.TaskStart("p", _dir);
            Assert.Equal(JsonValueKind.Null, Parse(json).GetProperty("reasoning_effort").ValueKind);

            var job = await WaitForDone(mgr.List().Single());
            Assert.False(job.Think);
            Assert.Null(job.ReasoningEffort);
            var ctk = TemplateKwargs(server).First();
            Assert.False((bool)ctk["enable_thinking"]!);
            Assert.Null(ctk["reasoning_effort"]);

            string result = await tools.TaskResult(job.Id);
            Assert.Equal(JsonValueKind.Null, Parse(result).GetProperty("reasoning_effort").ValueKind);
        }

        // ── devmind.json default, per-job override ───────────────────────────

        [Fact]
        public async Task DevmindJsonDefault_IsHonoured_AndPerJobValueOverridesIt()
        {
            WriteGlobalConfig("{ \"reasoningEffort\": \"XHIGH\" }");

            Assert.Null(AgentTaskTools.ResolveReasoningEffort(null, out string fromConfig));
            Assert.Equal("xhigh", fromConfig);
            Assert.Null(AgentTaskTools.ResolveReasoningEffort("low", out string perJob));
            Assert.Equal("low", perJob);

            // And end to end through a real start.
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();
            var tools = new AgentTaskTools(mgr);

            string json = await tools.TaskStart("p", _dir, think: true);
            Assert.Equal("xhigh", Parse(json).GetProperty("reasoning_effort").GetString());
            await WaitForDone(mgr.List().Single());
            Assert.Equal("xhigh", (string?)TemplateKwargs(server).Last()["reasoning_effort"]);
        }

        [Fact]
        public void DevmindJsonInvalidValue_FallsBackToMedium()
        {
            WriteGlobalConfig("{ \"reasoningEffort\": \"maximum\" }");

            Assert.Null(AgentTaskTools.ResolveReasoningEffort(null, out string effective));
            Assert.Equal("medium", effective);
        }

        // ── continuation inherits ────────────────────────────────────────────

        [Fact]
        public async Task Continuation_InheritsReasoningEffort()
        {
            using var server = new ThinkTokenLlmServer();
            Environment.SetEnvironmentVariable("DEVMIND_ENDPOINT", server.BaseUrl);
            using var mgr = new AgentJobManager();

            var parent = mgr.Start("p", _dir, 5, 30,
                allowCommit: false, verifyBuild: false, think: true, reasoningEffort: "xhigh");
            await WaitForDone(parent);
            int parentRequests = TemplateKwargs(server).Count;

            var child = mgr.Continue(parent.Id, "continue", 5, 30, verifyBuild: false, out string err);
            Assert.Null(err);
            Assert.NotNull(child);
            Assert.True(child.Think);
            Assert.Equal("xhigh", child.ReasoningEffort);

            await WaitForDone(child);
            var childKwargs = TemplateKwargs(server).Skip(parentRequests).ToList();
            Assert.NotEmpty(childKwargs);
            Assert.All(childKwargs, k => Assert.Equal("xhigh", (string?)k["reasoning_effort"]));
        }
    }
}
