// File: ToolCatalogueRegistryParityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Guards the invariant that every tool the system prompt advertises in its
// "## Tool Catalog" section actually has a schema entry in
// ToolRegistry.BuildToolsArray(). BuildToolsArray() is host-agnostic — the same
// array ships with every LlmClient request for TUI, console, and headless runs —
// so a tool the prompt names but the registry omits is advertised as usable and
// then rejected as unknown at call time. This is the durable half of the fix for
// the advertised-but-undeclared gap (learn_search / learn_fetch / learn_code_search):
// it fails the build the next time a tool is added to the prompt without a schema entry.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class ToolCatalogueRegistryParityTests
{
    // Advertised tools whose names contain no underscore. A regex cannot separate
    // these from ordinary prose words, so they are asserted explicitly. Keep in sync
    // if the catalogue gains or loses a single-word tool name.
    private static readonly string[] AdvertisedSingleWordTools = { "debug", "scratchpad", "hover" };

    // Snake_case tokens that appear in the catalogue but are NOT tools: parameter names
    // (start_line/end_line on the read_file line) and a debug sub-command value
    // (clear_breaks). Keep in sync if that vocabulary changes.
    private static readonly string[] NotATool = { "start_line", "end_line", "clear_breaks" };

    private static string[] RegistryToolNames() =>
        ToolRegistry.BuildToolsArray()
            .Select(t => (string?)t["function"]?["name"])
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

    private static string ToolCatalogSection(string prompt)
    {
        const string Start = "## Tool Catalog";
        int startAt = prompt.IndexOf(Start, StringComparison.Ordinal);
        Assert.True(startAt >= 0, "BuildToolUsePrompt is missing its '## Tool Catalog' section");
        int endAt = prompt.IndexOf("\n## ", startAt + Start.Length, StringComparison.Ordinal);
        if (endAt < 0)
            endAt = prompt.Length;
        return prompt.Substring(startAt, endAt - startAt);
    }

    // Derives the set of tool names the prompt advertises, straight from the
    // generated prompt — so it tracks the prose rather than a second hand-kept list.
    private static HashSet<string> AdvertisedTools()
    {
        string prompt = LoopHelpers.BuildToolUsePrompt(buildCommand: "", projectNamespace: "");
        string section = ToolCatalogSection(prompt);

        var advertised = new HashSet<string>(StringComparer.Ordinal);

        // Snake_case tools: every "- " catalogue bullet is scanned. Prose words have no
        // underscores; the NotATool list strips the few snake_case param/command tokens.
        var snake = new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");
        foreach (string line in section.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                continue;
            foreach (Match m in snake.Matches(line))
                if (!NotATool.Contains(m.Value, StringComparer.Ordinal))
                    advertised.Add(m.Value);
        }

        // Single-word tools a regex cannot isolate from prose.
        foreach (string t in AdvertisedSingleWordTools)
            advertised.Add(t);

        return advertised;
    }

    [Fact]
    public void EveryToolNamedInPromptCatalogue_HasASchemaEntry()
    {
        HashSet<string> registered = RegistryToolNames().ToHashSet(StringComparer.Ordinal);
        var missing = AdvertisedTools()
            .Where(t => !registered.Contains(t))
            .OrderBy(t => t)
            .ToList();

        Assert.True(missing.Count == 0,
            "Tools named in the prompt catalogue but missing from BuildToolsArray(): " +
            string.Join(", ", missing));
    }

    [Fact]
    public void LearnTools_AreAllRegistered()
    {
        var registered = RegistryToolNames().ToHashSet(StringComparer.Ordinal);
        foreach (string t in new[] { "learn_search", "learn_fetch", "learn_code_search" })
            Assert.True(registered.Contains(t), $"learn tool '{t}' is missing from BuildToolsArray()");
    }

    [Fact]
    public void ToolCount_ReflectsTheLearnToolRegistration()
    {
        // Expected registry size as of the learn_* registration (32 prior + 3 learn).
        // Update this when the tool set intentionally changes; the parity test above
        // is the real guard for advertised-but-undeclared drift.
        Assert.Equal(35, ToolRegistry.ToolCount);
    }
}
