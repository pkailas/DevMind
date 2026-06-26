// File: DebugResult.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Structured result types for DAP debugging. DevMind reasons over these as
// text — this is not a visual debugger. Produced by DapClient, rendered by
// the debug slash-command integration through the AppendOutput pipeline.

using System.Collections.Generic;

namespace DevMind
{
    /// <summary>Lifecycle state of a debug session.</summary>
    public enum DebugSessionState
    {
        Inactive,
        Initializing,
        Running,
        Stopped,
        Terminated,
    }

    /// <summary>A single stack frame in the debuggee.</summary>
    public sealed class DebugStackFrame
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string File { get; set; }   // source path; may be null (no symbols)
        public int Line { get; set; }
        public int Column { get; set; }
    }

    /// <summary>A variable scope (e.g. Locals, Arguments) for a frame.</summary>
    public sealed class DebugScope
    {
        public string Name { get; set; }
        public int VariablesReference { get; set; }
    }

    /// <summary>A single variable/value.</summary>
    public sealed class DebugVariable
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        /// <summary>&gt;0 when the value is expandable (object/collection).</summary>
        public int VariablesReference { get; set; }
    }

    /// <summary>Why and where execution stopped (breakpoint, step, exception, entry, pause).</summary>
    public sealed class BreakpointHit
    {
        public string Reason { get; set; }
        public int ThreadId { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        /// <summary>Extra detail (e.g. exception message); may be null.</summary>
        public string Description { get; set; }
        public DebugStackFrame TopFrame { get; set; }
    }

    /// <summary>Result of inspecting a named variable in the current frame.</summary>
    public sealed class VariableInspection
    {
        public bool Found { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public int VariablesReference { get; set; }
        public List<DebugVariable> Children { get; set; } = new List<DebugVariable>();
    }

    /// <summary>A captured call stack.</summary>
    public sealed class StackTraceResult
    {
        public List<DebugStackFrame> Frames { get; set; } = new List<DebugStackFrame>();
        public int TotalFrames { get; set; }
    }

    /// <summary>Result of evaluating an expression in the current frame.</summary>
    public sealed class EvaluateResult
    {
        public bool Success { get; set; }
        public string Expression { get; set; }
        public string Result { get; set; }
        public string Type { get; set; }
        public int VariablesReference { get; set; }
        public string Error { get; set; }
    }

    /// <summary>A line of debuggee/adapter output (stdout, stderr, console).</summary>
    public sealed class DebugOutput
    {
        public string Category { get; set; }
        public string Text { get; set; }
    }
}
