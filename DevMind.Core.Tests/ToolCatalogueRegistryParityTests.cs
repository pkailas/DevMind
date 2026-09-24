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

    // Every backticked identifier inside a "- " catalogue bullet is an advertised
    // tool name. The prompt wraps tool names in backticks (BuildToolUsePrompt), so
    // the advertised set is derived straight from the generated prompt and tracks
    // it rather than a second hand-kept list. Parameter names and debug sub-command
    // values are not backticked in the prompt, so they never surface here.
    private static readonly Regex BacktickedToolName = new(@"`([a-z][a-z0-9_]*)`");

    private static HashSet<string> AdvertisedToolsFromSection(string section)
    {
        var advertised = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in section.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                continue;
            foreach (Match m in BacktickedToolName.Matches(line))
                advertised.Add(m.Groups[1].Value);
        }
        return advertised;
    }

    private static HashSet<string> AdvertisedTools()
        => AdvertisedToolsFromSection(ToolCatalogSection(
            LoopHelpers.BuildToolUsePrompt(buildCommand: "", projectNamespace: "",
                                           workingDirectory: @"C:\work\repo")));

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

    // A single-word tool advertised in the catalogue but absent from the registry
    // must be reported as missing. Before catalogue names were backticked, a
    // single-word name could not be isolated from prose and such a tool slipped
    // through the guard silently. This drives the same derivation path the main
    // guard uses against a catalogue fragment containing an unregistered
    // single-word name and asserts it surfaces in the missing set.
    [Fact]
    public void AdvertisedSingleWordToolWithoutSchemaEntry_IsReportedMissing()
    {
        const string Fragment = "- Tracking state: `scratchpad`\n- New capability: `frob`\n";
        HashSet<string> advertised = AdvertisedToolsFromSection(Fragment);
        Assert.Contains("scratchpad", advertised);
        Assert.Contains("frob", advertised);

        var registered = RegistryToolNames().ToHashSet(StringComparer.Ordinal);
        var missing = advertised.Where(t => !registered.Contains(t)).OrderBy(t => t).ToList();
        Assert.Contains("frob", missing);
        Assert.DoesNotContain("scratchpad", missing);
    }

    // The derivation path above is proven end-to-end against the real generated
    // prompt: the single-word names a regex can no longer isolate from prose must
    // surface in the advertised set via their backticks. If a name loses its
    // backticks in the catalogue, it drops out of the derived set and this fails
    // by name — the guard would silently stop checking it otherwise.
    [Fact]
    public void AdvertisedTools_FromTheRealCatalogue_IncludeBacktickedSingleWordNames()
    {
        HashSet<string> advertised = AdvertisedTools();
        foreach (string t in new[] { "debug", "scratchpad", "hover" })
            Assert.True(advertised.Contains(t),
                $"'{t}' not found in the derived advertised set — is it backticked in the '## Tool Catalog' section?");
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
        // 35 = 34 tools built through MakeTool plus `debug`, which is hand-built (its schema
        // needs an enum command with a nested per-command args object) and added to the same
        // array. ToolCount is BuildToolsArray().Count, and RegistryToolNames() above reads the
        // same array, so the hand-built tool is inside both this count and the name-parity
        // check — an audit that counted MakeTool( occurrences by grep got 35 by including the
        // method's own definition and concluded debug sat outside; it does not. Update the
        // literal when the tool set intentionally changes; the parity test above is the real
        // guard for advertised-but-undeclared drift.
        Assert.Equal(35, ToolRegistry.ToolCount);
    }
}
