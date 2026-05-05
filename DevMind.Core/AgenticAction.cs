// File: AgenticAction.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    public enum ActionType
    {
        ApplyAndBuild,      // apply patches, then run shell command
        RunShell,           // execute a standalone shell command
        CreateFile,         // save FILE: content to disk
        Stop                // task complete or depth cap
    }

    /// <summary>
    /// Describes the next action the agentic loop should take.
    /// Output of the resolver, input to the executor.
    /// </summary>
    public class AgenticAction
    {
        public ActionType Type         { get; set; }
        public string     ShellCommand { get; set; }  // for RunShell
        public string     StopReason   { get; set; }  // for Stop — displayed to user

        public AgenticAction()
        {
            ShellCommand = string.Empty;
            StopReason   = string.Empty;
        }

        /// <summary>Returns a Stop action with the given reason.</summary>
        public static AgenticAction Stop(string reason)
        {
            return new AgenticAction
            {
                Type       = ActionType.Stop,
                StopReason = reason ?? string.Empty
            };
        }
    }
}
