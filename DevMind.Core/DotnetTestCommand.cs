// File: DotnetTestCommand.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The one `dotnet test` command line behind run_tests — BufferedAgenticHost (headless jobs),
// TuiAgenticHost and the MCP server's run_tests tool all build it here, so they cannot drift.
//
// H-11: it used to pass --no-build, so run_tests tested whatever was built LAST. After an
// edit the agent got the pre-edit result (job-1669: a fixed Core bug "still failing" because
// the test bin held the old Core.dll; job-1672's transcript shows the --no-build line). Without
// --no-build, `dotnet test` runs an incremental build of the test project and every project it
// references first; a build failure comes back as the tool result, with its errors, and no
// tests run.
//
// H-08: the blame-hang collector is always on, so a hung testhost is killed and named after
// 45 s instead of eating the whole run_tests timeout. The dump type is "none": the hang
// report (which test was running) is what the agent needs, and a full dump is hundreds of MB.

using System.Collections.Generic;

namespace DevMind
{
    public static class DotnetTestCommand
    {
        /// <summary>The hang guard every run_tests invocation carries (H-08).</summary>
        public static readonly IReadOnlyList<string> BlameHangArgs = new[]
        {
            "--blame-hang", "--blame-hang-timeout", "45s", "--blame-hang-dump-type", "none",
        };

        /// <summary>argv for <c>dotnet</c> (without the "dotnet" itself): test, the project if
        /// given, verbosity, the blame-hang guard, then the filter if given. Never --no-build.</summary>
        public static List<string> Arguments(string project, string filter)
        {
            var args = new List<string> { "test" };
            if (!string.IsNullOrWhiteSpace(project)) args.Add(project);
            args.Add("--verbosity");
            args.Add("normal");
            args.AddRange(BlameHangArgs);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }
            return args;
        }

        /// <summary>The same command as one shell line, for hosts that run it through the
        /// shell. A project path with spaces is quoted; the filter is always quoted (stray
        /// quotes the model put around it are stripped first).</summary>
        public static string CommandLine(string project, string filter)
        {
            string quotedProject = string.IsNullOrWhiteSpace(project) ? ""
                : project.Contains(' ') ? $" \"{project}\"" : " " + project;
            string filterArg = !string.IsNullOrWhiteSpace(filter) ? $" --filter \"{filter.Trim('"')}\"" : "";
            return $"dotnet test{quotedProject} --verbosity normal {string.Join(" ", BlameHangArgs)}{filterArg}";
        }
    }
}
