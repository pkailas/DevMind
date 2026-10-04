using System.Diagnostics;

namespace DevMind.TokenLedgerTray;

internal static class Program
{
    private const string MutexName = @"Local\DevMind.TokenLedgerTray";

    [STAThread]
    private static int Main(string[] args)
    {
        var selftest = HasFlag(args, "--selftest");

        // A WinExe has no console. AttachConsole(ATTACH_PARENT_PROCESS) gives
        // --selftest the caller's console so the output lands in the terminal
        // that ran it; --out <path> is the fallback for when there is no parent
        // console to attach to (Start-Process, a service, a double-click).
        var outPath = GetOption(args, "--out");
        var ledgerOverride = GetOption(args, "--ledger");
        var dateOverride = GetOption(args, "--date");

        if (selftest)
        {
            return RunSelfTest(ledgerOverride, dateOverride, outPath);
        }

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Silent exit: a second launch from Startup plus a manual launch must
            // not raise a dialog or a balloon.
            return 0;
        }

        var settings = TraySettings.Load();
        Application.Run(new TrayApplicationContext(settings));
        return 0;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Headless diagnostic path: prints resolved settings and the tooltip text for
    /// today plus any --date override, shows no UI, and exits 0.
    /// </summary>
    private static int RunSelfTest(string? ledgerOverride, string? dateOverride, string? outPath)
    {
        var settings = TraySettings.Load();
        var ledgerDir = string.IsNullOrWhiteSpace(ledgerOverride) ? settings.LedgerDir : ledgerOverride!;

        var lines = new List<string>();
        lines.Add("DevMind TokenLedgerTray selftest");
        lines.Add("LedgerDir:   " + ledgerDir);
        lines.Add("ScriptsDir:  " + settings.ScriptsDir);
        lines.Add("SettingsFile:" + (File.Exists(Path.Combine(AppContext.BaseDirectory, "TokenLedgerTray.json"))
            ? " present"
            : " absent (defaults)"));
        if (settings.SettingsError is not null)
        {
            lines.Add("SettingsError: " + settings.SettingsError);
        }

        lines.Add("daily.csv:   " + (File.Exists(Path.Combine(ledgerDir, "daily.csv")) ? "found" : "MISSING"));

        lines.Add("today:       " + LedgerSummary.BuildTooltip(ledgerDir, DateTime.Now));

        // The two pinned dates the brief verifies against the real ledger.
        lines.Add("2026-10-02:  " + LedgerSummary.BuildTooltip(ledgerDir, new DateTime(2026, 10, 2)));
        lines.Add("2026-09-20:  " + LedgerSummary.BuildTooltip(ledgerDir, new DateTime(2026, 9, 20)));

        if (!string.IsNullOrWhiteSpace(dateOverride))
        {
            if (DateTime.TryParseExact(dateOverride, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                lines.Add(dateOverride + ": " + LedgerSummary.BuildTooltip(ledgerDir, parsed));
            }
            else
            {
                lines.Add(dateOverride + ": (unparseable --date, expected yyyy-MM-dd)");
            }
        }

        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            File.WriteAllText(outPath!, text, System.Text.Encoding.UTF8);
            return 0;
        }

        if (Native.AttachConsole(Native.AttachParentProcess))
        {
            Console.Out.Write(text);
            Console.Out.Flush();
            return 0;
        }

        // No parent console: fall back to a file next to the exe rather than
        // silently producing nothing.
        var fallback = Path.Combine(AppContext.BaseDirectory, "selftest.out");
        File.WriteAllText(fallback, text, System.Text.Encoding.UTF8);
        return 0;
    }
}
