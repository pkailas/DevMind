// File: ProcessEnvironmentCollection.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One non-parallel xUnit collection for every test class that repoints a directory seam
// through the process environment — here DEVMIND_GLOBAL_DIR (DevMindPaths). The McpServer
// test assembly has the same collection for DEVMIND_TASKS_DIR as well.
//
// Environment variables are process-global. Two classes that each set and restore one
// can interleave — A saves X, B saves X, A sets Y, B restores X under A — and the loser
// writes into whatever directory the other left behind. When that directory is the
// production default, tests write into the live %APPDATA%\devmind folder (H-44).
// xunit.runner.json already runs this assembly's collections serially; this keeps the
// guarantee if that setting ever changes.

using Xunit;

namespace DevMind.Core.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProcessEnvironmentCollection
    {
        public const string Name = "ProcessEnvironment";
    }
}
