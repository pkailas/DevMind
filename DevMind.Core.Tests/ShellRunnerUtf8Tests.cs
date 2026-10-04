// File: ShellRunnerUtf8Tests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-07: shell output an agent can consume in place — UTF-8 end to end, no file round-trips.
//
// Before: [Console]::OutputEncoding was IBM437 and $OutputEncoding us-ascii inside the wrapper,
// and .NET decoded the pipes with the OEM code page, so "—" came back as "-", "✓" as "√", and a
// native tool's UTF-8 as "ΓÇö ├⌐"; `>` and `2>` wrote UTF-16LE (FF FE …); Write-Error reached
// stderr as a "#< CLIXML" blob. Agents redirected to %TEMP% files and read them back to get
// around it (jobs 1652/1653/1655). Real PowerShell / cmd.exe, a few hundred ms each.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ShellRunnerUtf8Tests : IDisposable
    {
        private const string Sample = "— é ✓ ┌─┐";
        private readonly string _dir;

        public ShellRunnerUtf8Tests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shutf8_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static bool CanRun => OperatingSystem.IsWindows() && ShellRunner.IsPowerShellAvailable();

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        [Fact]
        public async Task PowerShellOutput_NonAscii_ArrivesExact()
        {
            if (!CanRun) return;
            var (output, exit) = await new ShellRunner(_dir).ExecuteAsync($"Write-Output 'ps: {Sample}'", timeoutSeconds: 60);
            Assert.Equal(0, exit);
            Assert.Contains($"ps: {Sample}", output);
        }

        [Fact]
        public async Task NativeCommandOutput_Utf8_ArrivesExact()
        {
            if (!CanRun) return;
            File.WriteAllBytes(Path.Combine(_dir, "u8.txt"), new UTF8Encoding(false).GetBytes($"native: {Sample}\r\n"));
            var (output, exit) = await new ShellRunner(_dir).ExecuteAsync("cmd /c type u8.txt", timeoutSeconds: 60);
            Assert.Equal(0, exit);
            Assert.Contains($"native: {Sample}", output);
        }

        [Fact]
        public async Task CmdExePath_NonAscii_ArrivesExact()
        {
            if (!CanRun) return;
            // A first token of npm sends the whole command down the cmd.exe path (the .cmd shims).
            // Whether or not npm is installed, its own output is discarded; the echo is what is checked.
            var (output, _) = await new ShellRunner(_dir).ExecuteAsync($"npm --version >nul 2>nul & echo cmd: {Sample}", timeoutSeconds: 60);
            Assert.Contains($"cmd: {Sample}", output);
            Assert.DoesNotContain("code page", output, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RedirectToFile_WritesUtf8()
        {
            if (!CanRun) return;
            var (_, exit) = await new ShellRunner(_dir).ExecuteAsync("'é — x' > redirect.txt", timeoutSeconds: 60);
            Assert.Equal(0, exit);

            byte[] bytes = File.ReadAllBytes(Path.Combine(_dir, "redirect.txt"));
            // PS 5.1's 'utf8' writes a BOM; read_file strips it (PatchEngine.ReadFilePreservingEncoding).
            Assert.Equal(Utf8Bom.Concat(Encoding.UTF8.GetBytes("é — x\r\n")).ToArray(), bytes);
            Assert.Equal("é — x\r\n", PatchEngine.ReadFilePreservingEncoding(Path.Combine(_dir, "redirect.txt")).content);
        }

        [Fact]
        public async Task RedirectStderrToFile_WritesUtf8()
        {
            if (!CanRun) return;
            await new ShellRunner(_dir).ExecuteAsync("Write-Error 'é — boom' 2> err.txt", timeoutSeconds: 60);

            byte[] bytes = File.ReadAllBytes(Path.Combine(_dir, "err.txt"));
            Assert.Equal(Utf8Bom, bytes.Take(3).ToArray());
            Assert.DoesNotContain((byte)0, bytes);                                   // not UTF-16
            Assert.Contains("é — boom", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public async Task Stderr_FromANativeCommandAndFromWriteError_IsPlainText_NoClixml()
        {
            if (!CanRun) return;
            var runner = new ShellRunner(_dir);

            var (native, _) = await runner.ExecuteAsync("cmd /c \"echo native-err-h07 1>&2\"", timeoutSeconds: 60);
            Assert.Contains("native-err-h07", native);
            Assert.DoesNotContain("CLIXML", native);

            var (psError, _) = await runner.ExecuteAsync($"Write-Error 'boom-h07 {Sample}'", timeoutSeconds: 60);
            Assert.Contains($"boom-h07 {Sample}", psError);
            Assert.DoesNotContain("#< CLIXML", psError);
            Assert.DoesNotContain("<Objs", psError);
            Assert.DoesNotContain("_x000D__x000A_", psError);
        }

        [Fact]
        public void ClixmlToText_DecodesTheErrorRecord()
        {
            const string line =
                "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
                "<S S=\"Error\">Write-Error 'boom' : boom &amp; more_x000D__x000A_</S>" +
                "<S S=\"Error\">    + CategoryInfo          : NotSpecified: (:) [Write-Error], WriteErrorException_x000D__x000A_</S>" +
                "<S S=\"Error\"> _x000D__x000A_</S></Objs>";

            var text = ShellRunner.ClixmlToText(line);

            Assert.Equal(new[]
            {
                "Write-Error 'boom' : boom & more",
                "    + CategoryInfo          : NotSpecified: (:) [Write-Error], WriteErrorException",
            }, text);
            Assert.Null(ShellRunner.ClixmlToText("<Objs> not powershell </Objs>"));
            Assert.Null(ShellRunner.ClixmlToText("plain stderr line"));
        }

        [Fact]
        public async Task CmdRedirectionInsideTheWrapper_WritesTheLog()
        {
            // H-07 item 3 ("cmd /c "... > log 2>&1" wrote only 'The system cannot find the path
            // specified.' with exit 0", job-1655) is not the wrapper's doing: a cmd redirection inside it
            // writes its log. Kept as a guard. (That message is cmd's own for a batch file whose path
            // does not resolve — "is not recognized" when only the file is missing.)
            if (!CanRun) return;
            string log = Path.Combine(_dir, "cmd.log");
            var (output, exit) = await new ShellRunner(_dir).ExecuteAsync(
                $"cmd /c \"cd /d {_dir} && (echo hello-h07) > {log} 2>&1 & echo exitcode=%ERRORLEVEL%\"", timeoutSeconds: 60);

            Assert.Equal(0, exit);
            Assert.Contains("exitcode=0", output);
            Assert.Contains("hello-h07", File.ReadAllText(log));
        }
    }
}
