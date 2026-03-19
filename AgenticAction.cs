// File: AgenticAction.cs  v1.0.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;

namespace DevMind
{
    public enum ActionType
    {
        ApplyAndBuild,      // apply patches, then run shell command
        RunShell,           // execute a standalone shell command
        CreateFile,         // save FILE: content to disk
        LoadAndResubmit,    // auto-READ files, resubmit original prompt
        ContinueAgentic,    // feed results back to LLM, go around again
        RetryWithCorrection,// bare code detected, nudge the LLM
        Stop,               // task complete or depth cap
        AskUser             // need human confirmation
    }

    /// <summary>
    /// Describes the next action the agentic loop should take.
    /// Output of the resolver, input to the executor.
    /// </summary>
    public class AgenticAction
    {
        public ActionType   Type             { get; set; }
        public List<string> FilesToRead      { get; set; }  // for LoadAndResubmit
        public string       ShellCommand     { get; set; }  // for RunShell
        public string       StopReason       { get; set; }  // for Stop — displayed to user
        public string       CorrectionPrompt { get; set; }  // for RetryWithCorrection

        public AgenticAction()
        {
            FilesToRead      = new List<string>();
            ShellCommand     = string.Empty;
            StopReason       = string.Empty;
            CorrectionPrompt = string.Empty;
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

        /// <summary>Returns a ContinueAgentic action.</summary>
        public static AgenticAction Continue()
        {
            return new AgenticAction { Type = ActionType.ContinueAgentic };
        }

        /// <summary>Returns a LoadAndResubmit action targeting the specified files.</summary>
        public static AgenticAction Resubmit(List<string> files)
        {
            return new AgenticAction
            {
                Type        = ActionType.LoadAndResubmit,
                FilesToRead = files ?? new List<string>()
            };
        }

        /// <summary>Returns a RetryWithCorrection action with the given nudge prompt.</summary>
        public static AgenticAction Retry(string correctionPrompt)
        {
            return new AgenticAction
            {
                Type             = ActionType.RetryWithCorrection,
                CorrectionPrompt = correctionPrompt ?? string.Empty
            };
        }
    }
}
