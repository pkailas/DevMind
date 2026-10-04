# DevMind Token Ledger

Nightly accounting for what DevMind headless jobs cost. Four PowerShell scripts, no
C# involvement, designed to run under **Windows PowerShell 5.1** (`powershell.exe`,
not `pwsh`).

Every headless job writes `%LOCALAPPDATA%\Temp\devmind\tasks\<job-id>.result.json`.
`%TEMP%` gets cleaned, so the ledger keeps its own durable copy of every job it has
ever harvested.

## Files

| File | What it does |
| --- | --- |
| `Update-TokenLedger.ps1` | Harvests the sidecars into `jobs.csv`, then recomputes `daily.csv`, `daily-by-repo.csv` and `dashboard.html`, and appends to `ledger.log`. Idempotent - run it as many times a day as you like. |
| `dashboard.template.html` | The dashboard page template. `Update-TokenLedger.ps1` embeds the ledger data into it and writes `dashboard.html`. Edit this file to change the page; never edit `dashboard.html` by hand. |
| `Add-PlanUsage.ps1` | Manual logging of the Claude plan usage meters (Settings > Usage on claude.ai). There is no API for those percentages, so you type them in. Appends to `claude-plan-usage.csv`. |
| `Register-TokenLedgerTask.ps1` | Registers or replaces the scheduled task that runs the update daily at 23:50. **Run this elevated**; the driver does, not the ledger. |
| `README.md` | This file. |

## Ledger location

Default: `G:\DevMind_Tracing\ledger\` (created if missing; G: is a local drive).
Override with `-LedgerDir`. Point it at a scratch folder to test without touching
the real ledger.

### `jobs.csv` - the durable per-job ledger

One row per `job_id`. This is the source of truth; both rollups derive from it. Rows are
written sorted by `EndedUtc` then `JobId`, because the harvest builds them in a hashtable
whose enumeration order is unspecified - without the sort the file reshuffles on every
run and the diff is unreadable.

| Column | Meaning |
| --- | --- |
| `JobId` | Key. Repeat harvests upsert this row, never duplicate it. |
| `ParentJobId` | Blank for top-level jobs. |
| `EndedUtc` | Raw `ended_at_utc`, `yyyy-MM-dd HH:mm:ss`, UTC. |
| `EndedLocalDate` | `yyyy-MM-dd` in **machine local** time - this is the bucket the rollups group on. |
| `Repo` | Leaf of `working_dir`. |
| `WorkingDir` | Full path. |
| `State` | `done` / `stopped_incomplete` / `cancelled` / `failed`. |
| `HitDepthCap` | `true` / `false`. |
| `Iterations`, `ElapsedSeconds` | From the sidecar. |
| `TokensInTotal`, `TokensInNew`, `TokensOut` | **Blank when unknown.** Jobs from before token reporting (~Oct 1 2026) do not have these fields, and blank is not zero. |

Rows already in `jobs.csv` whose sidecar has since been deleted from `%TEMP%` are
**kept** - that is the whole point of the ledger. A sidecar that fails to parse is
skipped with a warning and never makes the run fail.

### `daily.csv` - one row per `EndedLocalDate`, sorted ascending

`Date`, `Jobs`, `Done`, `StoppedIncomplete`, `Cancelled`, `Failed`, `OtherState`,
`JobsWithUsage`, `JobsWithoutUsage`, `TokensInTotal`, `TokensInNew`,
`TokensInCached`, `TokensOut`, `Iterations`, `AgentMinutes`,
`OpusEquivUsdCached`, `OpusEquivUsdNoCache`.

Token sums and both `$` columns cover **only jobs with usage**. On a day where no
job has usage, those six columns are blank rather than `0`. `TokensInCached` is
`TokensInTotal - TokensInNew` per job. `AgentMinutes` is `ElapsedSeconds / 60` to
1 decimal, over all jobs that day.

### `daily-by-repo.csv`

`Date`, `Repo`, `Jobs`, `TokensInTotal`, `TokensOut`, `AgentMinutes`. Same blank
rule when none of that day's jobs for that repo have usage.

### `dashboard.html`

One self-contained page, rewritten on every run. Open it in Edge - it renders offline from
`file://`, so there is nothing to serve and nothing to configure.

All data is embedded as a single JSON block (`<script type="application/json">`), with inline
CSS, inline JS and inline SVG charts. There are **no external URLs, CDNs, web fonts or
images**; the only `http` in the file is the SVG namespace string in
`createElementNS`, which is an identifier, not a fetch. It is written UTF-8 **without BOM**
(a BOM before `<!DOCTYPE html>` drops Edge into quirks mode and breaks the layout) and
atomically (`.tmp` then `Move-Item -Force`).

What it shows:

| Section | Content |
| --- | --- |
| Header | Generated time, and the price line taken from the four price parameters. |
| KPI tiles | Last 7 days and last 30 days: jobs, agent hours, tokens in, tokens out, Opus-equivalent `$`, and **days without token data** - surfaced rather than folded into the totals. |
| Daily chart | Bars are `TokensInTotal` per day (left axis), line is `OpusEquivUsdCached` (right axis). A day with no token data is a hatched column labelled *no data*, **not a zero bar**. Hover for the day's numbers. |
| By repo | Last 30 days rolled up per repo, sorted by tokens in descending; em-dash when no job for that repo reported usage. |
| Daily detail | Last 30 days, newest first. Numbers right-aligned with thousands separators, blanks shown as an em-dash. |
| Plan usage | Table plus a `WeeklyPct` line chart when `claude-plan-usage.csv` has rows; otherwise the exact `Add-PlanUsage.ps1` command line to get started. |

Light and dark follow the system via `prefers-color-scheme` and CSS variables, and the layout
is responsive to window width. No frameworks.

To change the page, edit `dashboard.template.html` - the script replaces the placeholder line
`/*__LEDGER_DATA__*/` inside the JSON block with `ConvertTo-Json -Depth 5 -Compress` output,
with `</` escaped as `<\/` so a string in the data cannot close the block early. Numbers from
the CSVs become real JSON numbers and blanks become `null`, which is what lets the page tell
*unknown* from *zero*.

**A dashboard failure never fails the run.** The whole block is wrapped in `try/catch`: on a
problem it logs `dashboard: FAILED <message>` to `ledger.log` and still exits 0. The CSVs are
the ledger; the page is a view over them, so a broken template must not cost the harvest.

### `claude-plan-usage.csv`

`TimestampLocal` (`yyyy-MM-dd HH:mm`), `SessionPct`, `WeeklyPct`, `Note`. Header is
written automatically when the file is new.

### `ledger.log`

One line per run: timestamp, sidecars read, new jobs added, jobs total, parse
warning count.

## Price parameters

The two `$` columns are an *equivalence estimate* at Claude Opus 5.5 API list
prices per million tokens, **as of 2026-10-03**. They are default parameter values
on `Update-TokenLedger.ps1` - change them there, or pass them per run, when the
price sheet moves.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-InputPerM` | `4` | USD per 1M uncached input tokens |
| `-OutputPerM` | `20` | USD per 1M output tokens |
| `-CacheWritePerM` | `5` | USD per 1M cache-write input tokens |
| `-CacheHitPerM` | `0.20` | USD per 1M cache-hit input tokens |

```
OpusEquivUsdCached  = New*CacheWritePerM + Cached*CacheHitPerM + Out*OutputPerM   (per million)
OpusEquivUsdNoCache = Total*InputPerM    + Out*OutputPerM                        (per million)
```

`OpusEquivUsdCached` assumes everything was served through prompt caching (new
input billed at the cache-write rate, the rest at the cache-hit rate);
`OpusEquivUsdNoCache` is the worst case with no cache benefit at all. Real plan
cost is not this - that is what `Add-PlanUsage.ps1` is for. Both are rounded to
2 decimals.

## Logging plan usage

Read the two percentages off Settings > Usage on claude.ai and log them:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\TokenLedger\Add-PlanUsage.ps1 `
    -SessionPct 42 -WeeklyPct 61 -Note 'after the big refactor run'
```

`-SessionPct` and `-WeeklyPct` are required and validated to `0..100`; out of range
fails the script with exit code 1 and writes nothing. `-Note` and `-LedgerDir` are
optional.

## (Re)registering the scheduled task

Open an **elevated** Windows PowerShell and run:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\TokenLedger\Register-TokenLedgerTask.ps1
```

It creates or replaces the task `DevMind Token Ledger`:

- daily at **23:50** local, running `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<full path of Update-TokenLedger.ps1>"`
- principal: current user, **LogonType Interactive** - runs while you are logged on, stores
  no password; a run missed while logged off is caught up at next logon (StartWhenAvailable).
  S4U ("run whether or not logged on") fails on BEAST for KAILAS\pkailas: LogonUserS4U returns
  0x80070032 ERROR_NOT_SUPPORTED (Task Scheduler event 104).
- `StartWhenAvailable`: a run missed because the machine was off or asleep is caught
  up when it comes back
- `ExecutionTimeLimit`: 30 minutes

`-Force` is used, so re-running replaces the task. Change the time with `-TimeOfDay`,
or the name with `-TaskName`. To remove it:

```
Unregister-ScheduledTask -TaskName 'DevMind Token Ledger' -Confirm:$false
```

## PowerShell 5.1 constraints

These scripts are written for 5.1 and break on 7-only syntax, so keep it that way:

- **ASCII-only source** - no em-dashes or smart quotes (they mojibake under 5.1).
- **No `?:`, `??`, `&&`, `||`, `-Parallel`.**
- **Hashtable splatting only**, never array splatting.
- **Wrap pipeline results in `@(...)`** before `.Count` or indexing.
- `ConvertFrom-Json` has **no `-Json` parameter** in 5.1 - bind the JSON positionally.
- `[Parameter(Mandatory = $true)]` on an `[object]` parameter **rejects `$null`** in
  5.1; `Get-Field` in the update script is deliberately non-mandatory so the
  optional `{ "result": { ... } }` unwrap can pass `$null`.
- `Set-StrictMode` is deliberately **not** used: pre-Oct-2026 jobs have no
  `tokens_in_total` property at all, and reading a missing property is the normal
  path here, not a bug.
- `[math]::Round` returns an **int** when handed one, which is why the money columns
  cast through `-as [decimal]` before rounding or a genuine `0` prints as `0`
  instead of `0.00`.

## Note on date bucketing

`ended_at_utc` is UTC. The rollups bucket on the **machine local** date, because the
ledger answers "what did I burn today". With a US Eastern machine that shifts the
boundary by 4 or 5 hours; a job ending at 01:00 UTC on the 3rd lands on the 2nd.
Bucketing on the raw UTC date instead gives materially different daily totals, so
do not "fix" this.
