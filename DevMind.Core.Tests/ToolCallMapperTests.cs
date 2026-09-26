// File: ToolCallMapperTests.cs
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Covers the patch_file tool-call → PATCH block conversion, including the batched
// edits[] form (the headless path the local model actually drives).

using System.Collections.Generic;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class ToolCallMapperTests
{
    private static ResponseBlock MapPatch(Dictionary<string, string> args)
    {
        var tc = new ToolCallResult { Name = "patch_file", Arguments = args };
        var blocks = ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build");
        return Assert.Single(blocks);
    }

    [Fact]
    public void PatchFile_EditsArray_ProducesMultiPairPatchBlock()
    {
        var block = MapPatch(new Dictionary<string, string>
        {
            ["filename"] = "C:\\repo\\Foo.cs",
            ["edits"] =
                "[{\"find\":\"aaa\",\"replace\":\"AAA\"}," +
                " {\"find\":\"bbb\",\"replace\":\"BBB\"}," +
                " {\"find\":\"ccc\",\"replace\":\"CCC\"}]"
        });

        Assert.Equal(BlockType.Patch, block.Type);

        // The generated PATCH text must parse back to exactly the three pairs, in order.
        var pairs = PatchEngine.ParsePatchBlocks(block.Content, fromToolCall: true);
        Assert.Equal(3, pairs.Count);
        Assert.Equal(("aaa", "AAA"), pairs[0]);
        Assert.Equal(("bbb", "BBB"), pairs[1]);
        Assert.Equal(("ccc", "CCC"), pairs[2]);
    }

    [Fact]
    public void PatchFile_SingleFindReplace_StillWorks()
    {
        var block = MapPatch(new Dictionary<string, string>
        {
            ["filename"] = "C:\\repo\\Foo.cs",
            ["find"]     = "old text",
            ["replace"]  = "new text"
        });

        var pairs = PatchEngine.ParsePatchBlocks(block.Content, fromToolCall: true);
        Assert.Equal(("old text", "new text"), Assert.Single(pairs));
    }

    [Fact]
    public void PatchFile_MalformedEdits_FallsBackToFindReplace()
    {
        var block = MapPatch(new Dictionary<string, string>
        {
            ["filename"] = "C:\\repo\\Foo.cs",
            ["edits"]    = "not valid json",
            ["find"]     = "fallback find",
            ["replace"]  = "fallback replace"
        });

        var pairs = PatchEngine.ParsePatchBlocks(block.Content, fromToolCall: true);
        Assert.Equal(("fallback find", "fallback replace"), Assert.Single(pairs));
    }

    // ── run_shell: detach (H-24) ─────────────────────────────────────────────
    // Tool-call arguments arrive as strings; a JSON boolean stringifies as "True"/"False".

    private static ResponseBlock MapShell(Dictionary<string, string> args)
    {
        var tc = new ToolCallResult { Name = "run_shell", Arguments = args };
        var blocks = ToolCallMapper.Map(new List<ToolCallResult> { tc }, buildCommand: "dotnet build");
        return Assert.Single(blocks);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void RunShell_DetachPresent_IsMapped(string value, bool expected)
    {
        var block = MapShell(new Dictionary<string, string> { ["command"] = "Start-Process app.exe", ["detach"] = value });

        Assert.Equal(BlockType.Shell, block.Type);
        Assert.Equal("Start-Process app.exe", block.Command);
        Assert.Equal(expected, block.ShellDetach);
    }

    [Fact]
    public void RunShell_DetachAbsent_IsFalse_AndTimeoutStillMapped()
    {
        var block = MapShell(new Dictionary<string, string> { ["command"] = "dotnet build", ["timeout_seconds"] = "300" });

        Assert.False(block.ShellDetach);
        Assert.Equal(300, block.ShellTimeoutSeconds);
    }
}
