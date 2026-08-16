Headless narration-retry flag fix (DevMind.Core/HeadlessAgent.cs, HeadlessSession.RunTurnAsync):

The TUI loop (DevMind.TUI/Program.cs ~1360/1416/1574) reads LoopIterationResult.ForceToolChoiceRequired and passes it into LlmClient.SendMessageAsync(forceToolChoiceRequired: ...). The headless loop was NOT reading it — the field was silently dropped, so a mid-session narration stall (prose claim like "let me check the build" / "build: 0 errors", no tool call, inside an agentic cycle) never got tool_choice="required" re-issued in headless mode; it ended with an unverified narration as the final answer. LoopDriver's guard (LoopDriver.cs ~450-467, NARRATION_RETRY_ENABLED, MatchNarrationClaim) produces the flag via MakeShouldReTrigger(forceToolChoiceRequired: true).

Fix (3 sites, symmetric with TUI):
- declare `bool forceToolChoiceRequired = false;` beside currentPrompt
- pass `forceToolChoiceRequired: forceToolChoiceRequired` into SendMessageAsync
- assign `forceToolChoiceRequired = iter.ForceToolChoiceRequired;` (ASSIGN not OR, so a later non-forcing re-trigger clears it; it must not latch) in the ShouldReTrigger branch

LlmClient.BuildRequestJson always emits tool_choice: cold start (0 tool-result msgs in history) => "required" (Layer 1); else "auto". So a MID-SESSION "required" can only come from the forwarded flag — that's what the tests assert.

Tests (DevMind.Core.Tests/HeadlessAgentTests.cs) observe the outgoing flag via FakeSseServer.RequestBodies (raw POST bodies; LlmClient uses Newtonsoft JObject.ToString(Formatting.None) => real quotes, so assert literal "\"tool_choice\":\"required\"" — NOT backslash-escaped):
- RunAsync_NarrationStall_FollowUpRequestSendsForcedToolChoiceThenClears: create_file -> narration prose -> run_shell (assert req[2] required = the fix) -> task_done (assert req[3] auto = flag cleared). 4 iterations/bodies.
- RunAsync_NarrationClaimAfterForcedRetry_SendsNoSecondForcedRequest: the retry's own prose (>=20 chars, in-cycle) still trips the ProseFinish re-prompt (one-shot, NOT a second forced retry because NarrationRetryUsed is spent), so 4 iterations; only 2 bodies are "required" (cold start + the one retry), req[1] and req[3] are "auto".
Both FAIL without the fix (verified via git stash of HeadlessAgent.cs -> 2 failed, 0 passed) and pass with it. Full suite: 374/374 (was 372).

Gotcha: `dotnet test --no-build` can run a STALE dll if a prior build didn't pick up your latest patch (I saw "Expected: 3 / line 312" from the pre-patch file even after patching). When results look inconsistent with the on-disk source, explicitly `dotnet build` the test project, then re-run.