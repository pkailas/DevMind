using System.Text;

namespace DevMind.TokenLedgerTray;

/// <summary>
/// One row of daily.csv. Every value is the raw CSV cell string: Export-Csv
/// quotes everything and writes blank ("") for unknown, and blank is NOT zero
/// (days before token reporting have no token columns at all).
/// </summary>
public sealed record DailyRow(IReadOnlyDictionary<string, string> Fields)
{
    public string Date => Get("Date");
    public string Jobs => Get("Jobs");
    public string TokensInTotal => Get("TokensInTotal");
    public string OpusEquivUsdCached => Get("OpusEquivUsdCached");

    private string Get(string name) =>
        Fields.TryGetValue(name, out var v) ? v : string.Empty;
}

/// <summary>
/// Parses and formats the DevMind token ledger CSVs. Static and side-effect free
/// (beyond reading a file) so the tooltip logic is testable headlessly via
/// --selftest without any UI.
/// </summary>
public static class LedgerSummary
{
    /// <summary>NotifyIcon.Text is capped at 127 characters by WinForms.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>
    /// Minimal robust reader for Export-Csv output: RFC-4180 quoting, CRLF or LF,
    /// embedded quotes doubled ("") and newlines inside quotes. Fields are mapped
    /// by header name, so column order in the file does not matter.
    /// No NuGet dependency by design.
    /// </summary>
    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path))
        {
            return rows;
        }

        // ASCII per the ledger scripts, but read as UTF-8: every byte of ASCII is
        // valid UTF-8, and this also survives a stray BOM being added later.
        var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        var records = ParseRecords(text);
        if (records.Count == 0)
        {
            return rows;
        }

        var header = records[0];
        for (var i = 1; i < records.Count; i++)
        {
            var cells = records[i];

            // Skip a trailing blank line: one empty field with an empty value is
            // not a data row.
            if (cells.Count == 1 && cells[0].Length == 0)
            {
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Count && c < cells.Count; c++)
            {
                row[header[c]] = cells[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Splits CSV text into records of field strings.</summary>
    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            records.Add(record);
            record = new List<string>();
        }

        while (i < text.Length)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    // "" inside a quoted field is a literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(ch);
                i++;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;

                case ',':
                    EndField();
                    i++;
                    break;

                case '\r':
                    // CRLF, or a lone CR.
                    EndRecord();
                    i++;
                    if (i < text.Length && text[i] == '\n')
                    {
                        i++;
                    }

                    break;

                case '\n':
                    EndRecord();
                    i++;
                    break;

                default:
                    field.Append(ch);
                    i++;
                    break;
            }
        }

        // Final record without a trailing newline.
        if (field.Length > 0 || record.Count > 0)
        {
            EndRecord();
        }

        return records;
    }

    /// <summary>
    /// The daily.csv row for a local date, or null when that day has no row.
    /// Date cells are "yyyy-MM-dd"; compared as strings, so parsing is culture-safe.
    /// </summary>
    public static DailyRow? FindDay(string ledgerDir, string dateKey)
    {
        var path = Path.Combine(ledgerDir, "daily.csv");
        foreach (var fields in ReadCsv(path))
        {
            if (!fields.TryGetValue("Date", out var date))
            {
                continue;
            }

            if (string.Equals(date.Trim(), dateKey, StringComparison.Ordinal))
            {
                return new DailyRow(fields);
            }
        }

        return null;
    }

    /// <summary>
    /// Tray tooltip for a given local date.
    ///   no row            -> "DevMind today: no jobs yet"
    ///   row, blank tokens -> "DevMind today: N jobs, no token data"
    ///   row with tokens   -> "DevMind today: N jobs, 12.3M tok in, $X Opus-eq"
    /// A date other than today still says "today" per the brief's wording, but
    /// selftest passes explicit dates to pin the formatting.
    /// </summary>
    public static string BuildTooltip(string ledgerDir, DateTime localDate)
    {
        var dateKey = localDate.ToString("yyyy-MM-dd");
        var row = FindDay(ledgerDir, dateKey);
        return Compose(row);
    }

    /// <summary>Composes the tooltip from an already-resolved row.</summary>
    public static string Compose(DailyRow? row)
    {
        if (row is null)
        {
            return Clamp("DevMind today: no jobs yet");
        }

        var jobs = row.Jobs.Trim();
        if (jobs.Length == 0)
        {
            jobs = "0";
        }

        var tokens = row.TokensInTotal.Trim();
        if (tokens.Length == 0)
        {
            // The day has jobs but nothing reported usage: the six token/$
            // columns are blank, which is unknown, not zero.
            return Clamp($"DevMind today: {jobs} jobs, no token data");
        }

        var money = row.OpusEquivUsdCached.Trim();
        var moneyPart = money.Length == 0 ? "n/a" : "$" + money;

        return Clamp($"DevMind today: {jobs} jobs, {FormatTokens(tokens)} tok in, {moneyPart} Opus-eq");
    }

    /// <summary>
    /// 275703327 -> "275.7M". Invariant culture: the CSV is written invariant.
    /// K/M/B suffixes, one decimal. Values under 1000 print as whole numbers.
    /// </summary>
    public static string FormatTokens(string rawNumber)
    {
        if (!decimal.TryParse(rawNumber,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return rawNumber;
        }

        return FormatTokens(value);
    }

    public static string FormatTokens(decimal value)
    {
        var sign = value < 0m ? "-" : string.Empty;
        var abs = Math.Abs(value);

        if (abs >= 1_000_000_000m)
        {
            return sign + (abs / 1_000_000_000m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "B";
        }

        if (abs >= 1_000_000m)
        {
            return sign + (abs / 1_000_000m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }

        if (abs >= 1_000m)
        {
            return sign + (abs / 1_000m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "K";
        }

        return sign + abs.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Truncates to NotifyIcon.Text's 127-character limit.</summary>
    public static string Clamp(string text)
    {
        if (text.Length <= MaxTooltipLength)
        {
            return text;
        }

        return text.Substring(0, MaxTooltipLength - 1) + "...";
    }
}
