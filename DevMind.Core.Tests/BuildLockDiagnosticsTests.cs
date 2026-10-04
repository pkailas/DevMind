// File: BuildLockDiagnosticsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-68: reading MSBuild's file-lock diagnostics. The lines below are verbatim from job-2152 — the
// verification rebuild's output (result sidecar) and the agent's own build in the transcript, where
// the console wrapped each MSB3061 diagnostic over several lines.

using Xunit;

namespace DevMind.Core.Tests
{
    public class BuildLockDiagnosticsTests
    {
        // Verbatim from job-2152.result.json build_verification.output_tail.
        private const string Msb3026 =
            @"C:\Program Files\dotnet\sdk\10.0.302\Microsoft.Common.CurrentVersion.targets(5096,5): warning MSB3026: Could not copy ""C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizer.Shared\bin\Debug\net10.0\VLink.PDFSanitizer.Shared.dll"" to ""bin\Debug\net10.0-windows10.0.19041.0\win-x64\VLink.PDFSanitizer.Shared.dll"". Beginning retry 10 in 1000ms. The process cannot access the file 'C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizerConfig\bin\Debug\net10.0-windows10.0.19041.0\win-x64\VLink.PDFSanitizer.Shared.dll' because it is being used by another process. The file is locked by: ""devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868)"" [C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizerConfig\VLink.PDFSanitizerConfig.csproj]";

        private const string Msb3027 =
            @"C:\Program Files\dotnet\sdk\10.0.302\Microsoft.Common.CurrentVersion.targets(5096,5): error MSB3027: Could not copy ""C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizer.Shared\bin\Debug\net10.0\VLink.PDFSanitizer.Shared.dll"" to ""bin\Debug\net10.0-windows10.0.19041.0\win-x64\VLink.PDFSanitizer.Shared.dll"". Exceeded retry count of 10. Failed. The file is locked by: ""devenv.exe (116488), VLink.PDFSanitizerConfig.exe (83868)"" [C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizerConfig\VLink.PDFSanitizerConfig.csproj]";

        private const string Msb3021 =
            @"C:\Program Files\dotnet\sdk\10.0.302\Microsoft.Common.CurrentVersion.targets(5096,5): error MSB3021: Unable to copy file ""C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizer.Shared\bin\Debug\net10.0\VLink.PDFSanitizer.Shared.dll"" to ""bin\Debug\net10.0-windows10.0.19041.0\win-x64\VLink.PDFSanitizer.Shared.dll"". The process cannot access the file 'C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizerConfig\bin\Debug\net10.0-windows10.0.19041.0\win-x64\VLink.PDFSanitizer.Shared.dll' because it is being used by another process. [C:\Users\pkailas\source\repos\VLink.PDFSanitizer\VLink.PDFSanitizerConfig\VLink.PDFSanitizerConfig.csproj]";

        // Verbatim from the job-2152 transcript: one MSB3061 diagnostic, wrapped by the console, with
        // the locker's pid on the next physical line.
        private const string Msb3061Wrapped =
            "C:\\Program Files\\dotnet\\sdk\\10.0.302\\Microsoft.Common.CurrentVersion.targets(5953,5): warning MSB3061: Unable to delete file \n" +
            "\"C:\\Users\\pkailas\\source\\repos\\VLink.PDFSanitizer\\VLink.PDFSanitizerConfig\\bin\\Debug\\net10.0-windows10.0.19041.0\\win-x64\\dcompi.dll\". Access to the path \n" +
            "'C:\\Users\\pkailas\\source\\repos\\VLink.PDFSanitizer\\VLink.PDFSanitizerConfig\\bin\\Debug\\net10.0-windows10.0.19041.0\\win-x64\\dcompi.dll' is denied. The file is locked by: \"VLink.PDFSanitizerConfig.exe \n" +
            "(83868)\" [C:\\Users\\pkailas\\source\\repos\\VLink.PDFSanitizer\\VLink.PDFSanitizerConfig\\VLink.PDFSanitizerConfig.csproj]";

        [Fact]
        public void TheRealMessages_YieldTheLockingProcesses_AndCounts()
        {
            string output = Msb3026 + "\r\n" + Msb3027 + "\r\n" + Msb3021 + "\r\n\r\nBuild FAILED.\r\n\r\n" +
                            Msb3026 + "\r\n" + Msb3027 + "\r\n" + Msb3021 + "\r\n    1 Warning(s)\r\n    2 Error(s)\r\n";

            var r = BuildLockDiagnostics.Analyze(output);

            Assert.Equal(new[] { "devenv.exe (116488)", "VLink.PDFSanitizerConfig.exe (83868)" }, r.LockedBy);
            Assert.Equal(1, r.LockWarningCount);          // the summary's repeats are not counted again
            Assert.Equal(2, r.LockErrorCount);
            Assert.Equal(0, r.OtherErrorCount);
            Assert.Equal(3, r.LockLines.Count);
            Assert.DoesNotContain(r.LockLines, l => l.EndsWith(".csproj]"));   // project suffix removed for quoting
        }

        [Fact]
        public void AWrappedDiagnostic_IsReadWhole()
        {
            var r = BuildLockDiagnostics.Analyze(Msb3061Wrapped + "\n");

            Assert.Equal(1, r.LockWarningCount);
            Assert.Equal(new[] { "VLink.PDFSanitizerConfig.exe (83868)" }, r.LockedBy);
        }

        [Fact]
        public void NonLockErrors_AreCountedApart()
        {
            const string cs = @"C:\src\Program.cs(4,9): error CS0103: The name 'Baz' does not exist in the current context [C:\src\App.csproj]";
            var r = BuildLockDiagnostics.Analyze(cs + "\n" + Msb3027 + "\n");

            Assert.Equal(1, r.OtherErrorCount);
            Assert.Equal(1, r.LockErrorCount);
            Assert.Single(r.OtherErrorLines);
        }

        [Theory]
        [InlineData(Msb3026, true)]
        [InlineData(Msb3027, true)]
        [InlineData(Msb3021, true)]
        [InlineData(@"C:\src\Model.cs(1,1): warning CS8618: Non-nullable property [C:\src\App.csproj]", false)]
        [InlineData("Build succeeded.", false)]
        public void IsLockLine(string line, bool expected) => Assert.Equal(expected, BuildLockDiagnostics.IsLockLine(line));

        [Fact]
        public void NothingToRead_IsNone()
        {
            Assert.Equal(0, BuildLockDiagnostics.Analyze("").LockWarningCount);
            Assert.Empty(BuildLockDiagnostics.Analyze("Build succeeded.\n    0 Warning(s)\n").LockedBy);
        }
    }
}
