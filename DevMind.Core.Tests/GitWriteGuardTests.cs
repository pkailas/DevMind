// File: GitWriteGuardTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-59: the headless shell guard blocked read-only `git show HEAD:<path>` (job-2115: the quoted
// '=>' in a Where-Object filter; job-2116: "p>" in a Select-String regex). Reading git objects
// is allowed; writing their content onto a working-tree file is not.

using Xunit;

namespace DevMind.Core.Tests
{
    public class GitWriteGuardTests
    {
        private const string Wd = @"C:\repo\wd";

        private static string? Blocked(string command)
            => BufferedAgenticHost.IsBlockedHeadlessCommand(command, out string? reason, Wd) ? reason : null;

        // Verbatim (modulo the cd target) from the two blocked jobs.
        private const string Job2115 =
            "cd C:\\repo\\wd\n" +
            "$f = \"src/Verbella.VLink.Warehouses.Service/Pages/Admin/Config/Bindings/Index.cshtml\"\n" +
            "$head = git show \"HEAD:$f\"\n" +
            "$new  = Get-Content $f -Raw\n" +
            "function Toks($t) { [regex]::Matches($t,'class=\"([^\"]*)\"') | ForEach-Object { $_.Groups[1].Value -split '\\s+' } | Where-Object { $_ } | Sort-Object -Unique }\n" +
            "$h = Toks $head; $n = Toks $new\n" +
            "\"=== tokens NEW but not in HEAD page (i.e. I introduced them) ===\"\n" +
            "Compare-Object $h $n | Where-Object SideIndicator -eq '=>' | ForEach-Object { $_.InputObject }\n" +
            "\"=== tokens in HEAD page but no longer used by me (i.e. removed) ===\"\n" +
            "Compare-Object $h $n | Where-Object SideIndicator -eq '<=' | ForEach-Object { $_.InputObject }";

        private const string Job2116 =
            "cd C:\\repo\\wd; git show HEAD:src/Verbella.VLink.Warehouses.Service/Pages/Admin/Config/Backup/Index.cshtml " +
            "| Select-String -Pattern \"Restore&hellip;|Restore…|style=\" ; echo ---; " +
            "git show HEAD:src/Verbella.VLink.Warehouses.Service/Pages/Admin/Config/ApiKeys/Index.cshtml " +
            "| Select-String -Pattern \"p>|label|select|input\"";

        [Theory]
        [InlineData(Job2115)]
        [InlineData(Job2116)]
        [InlineData("git show HEAD:a.cs")]
        [InlineData("$h = git show HEAD:a.cs; ($h | Measure-Object -Line).Lines")]
        [InlineData("git show HEAD:a.cs | Select-String 'class=\"x\">'")]
        [InlineData("Compare-Object (git show HEAD:a.cs) (Get-Content a.cs)")]
        [InlineData("git show HEAD:a.cs 2>&1")]
        [InlineData("git show HEAD:a.cs 2>$null | Select-String foo")]
        [InlineData("git show HEAD:a.cs > $null")]
        [InlineData("git show HEAD:a.cs | Tee-Object -Variable h")]
        [InlineData("git show HEAD:a.cs > $env:TEMP\\head_a.cs")]
        [InlineData("git show HEAD:a.cs | Set-Content \"$env:TEMP\\head_a.cs\"")]
        [InlineData("git show HEAD:a.cs | Out-File -FilePath %TEMP%\\head_a.cs")]
        [InlineData("git show HEAD:a.cs > C:\\elsewhere\\a.cs")]
        [InlineData("git diff HEAD -- a.cs > a.patch")]            // not object content
        [InlineData("$h = git show HEAD:a.cs; $h = 'x'; Set-Content a.cs $h")] // reassigned, no longer git content
        [InlineData("git checkout main")]
        [InlineData("git checkout -b feature")]
        public void ReadOnlyOrOutsideTheWorkingDir_IsAllowed(string command)
        {
            Assert.Null(Blocked(command));
        }

        [Theory]
        [InlineData("git show HEAD:a.cs > a.cs")]
        [InlineData("git show HEAD:a.cs >> a.cs")]
        [InlineData("git show HEAD:a.cs | Set-Content a.cs")]
        [InlineData("git show HEAD:a.cs | sc a.cs")]
        [InlineData("git show HEAD:a.cs | Out-File -FilePath src\\a.cs -Encoding utf8")]
        [InlineData("git show HEAD:a.cs | Out-File -Encoding utf8 a.cs")]
        [InlineData("git show HEAD:a.cs | Add-Content -Path a.cs")]
        [InlineData("git show HEAD:a.cs | Tee-Object -FilePath a.cs")]
        [InlineData("git show HEAD~1:file | Set-Content file")]   // job-11
        [InlineData("git show \"HEAD:$f\" | Set-Content $f")]      // unresolvable target counts as inside
        [InlineData("git show HEAD:a.cs > C:\\repo\\wd\\src\\a.cs")]
        [InlineData("cd src; git show HEAD:src/a.cs > a.cs")]
        [InlineData("git cat-file -p HEAD:a.cs > a.cs")]
        [InlineData("git show HEAD:a.cs --output=a.cs")]
        [InlineData("$h = git show HEAD:a.cs; Set-Content a.cs $h")]
        [InlineData("$h = git show HEAD:a.cs\n$h | Out-File a.cs")]
        [InlineData("$h = git show HEAD:a.cs; [IO.File]::WriteAllText('C:\\repo\\wd\\a.cs', $h)")]
        [InlineData("Get-ChildItem *.cs | ForEach-Object { git show \"HEAD:$($_.Name)\" > $_.Name }")]
        public void GitObjectContentWrittenIntoTheWorkingTree_IsBlocked(string command)
        {
            Assert.Equal(GitWriteGuard.WriteReason, Blocked(command));
        }

        [Theory]
        [InlineData("git checkout HEAD -- a.cs")]
        [InlineData("git checkout 1a2b3c4 -- a.cs")]   // missed by the old substring rule
        [InlineData("git checkout -- a.cs")]
        [InlineData("git checkout HEAD a.cs")]
        [InlineData("git checkout 1a2b3c4 a.cs")]
        [InlineData("git restore --source HEAD~1 a.cs")]
        [InlineData("git -C C:\\repo\\wd restore a.cs")]
        [InlineData("git reset --hard")]
        [InlineData("git clean -fd")]
        public void RestoresFromGit_AreBlocked(string command)
        {
            Assert.Equal(GitWriteGuard.RestoreReason, Blocked(command));
        }

        [Fact]
        public void TempIsOutsideTheWorkingDir_ForTheTempRedirectCases()
        {
            // The %TEMP% cases above rely on the working dir not being under %TEMP%.
            Assert.False(System.IO.Path.GetFullPath(Path.GetTempPath())
                .StartsWith(Wd, StringComparison.OrdinalIgnoreCase));
        }
    }
}
