Delegated-task (MCP devmind_task_*) state signalling — needs_input path and a dead-field gotcha.

needs_input state is derived ONLY from the model calling ask_caller:
ToolCallMapper.cs (case "ask_caller") -> BlockType.NeedsInput -> ResponseOutcome.IsNeedsInput
-> LoopDriver.cs (terminal reason "needs_input") -> HeadlessAgent RunTurnAsync sets
result.NeedsInput -> AgentJobManager/AgentTaskTools.DisplayState maps to "needs_input".
Continuation is NOT gated on needs_input (AgentTaskTools can_continue = Session!=null
&& ContinuedByJobId==null), so the caller can always answer — the only failure mode is
the caller not being TOLD (task ends "done" with questions buried in the prose answer).

LoopDriver has a STRUCTURAL one-shot re-prompt (no regex) at the no-tool-call terminal
gate: when the model ends in prose with no tool call (prosePresent) inside an agentic
cycle, it fires SyntheticPrompts.ProseFinish (gated by LoopState.PromptedForTaskDone,
one-shot). As of 2026 the ProseFinish text presents BOTH terminal tools (task_done AND
ask_caller) so the model makes the done-vs-needs_input decision at the stop point. The
prosePresent length gate is >= 20 (was > 40) so short question-only endings reach it.
A previous prose-regex heuristic to infer "done-with-questions" was REJECTED in review
for misfiring on narration — do not reintroduce regex-based intent inference here.

GOTCHA (discovered 2026): LoopIterationResult.ForceToolChoiceRequired is IGNORED in the
headless/delegated path. HeadlessSession.RunTurnAsync only consumes
iter.NextContextualMessage (HeadlessAgent.cs ~line 376) and never reads
ForceToolChoiceRequired before the next SendMessageAsync. So the narration retry's
forceToolChoiceRequired:true is silently a no-op in devmind_task_* runs. LlmClient DOES
support tool_choice=required (BuildRequestJson sets request["tool_choice"]="required"
when forceToolChoiceRequired) — it just isn't wired through in headless. Do not assume
forceToolChoiceRequired works in the MCP job path.