using System.Diagnostics;

namespace DevMind.TokenLedgerTray;

/// <summary>
/// The tray itself: NotifyIcon, context menu, 10-minute tooltip timer and a
/// debounced FileSystemWatcher on daily.csv. No form, so the application must be
/// run with Application.Run(ILifetimeContext) - that keeps the message loop alive
/// without a window.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(5);

    private readonly TraySettings _settings;
    private readonly NotifyIcon _notify;
    private readonly TrayIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openDashboard;
    private readonly ToolStripMenuItem _refresh;
    private readonly ToolStripMenuItem _planUsage;
    private readonly System.Windows.Forms.Timer _tooltipTimer;
    private readonly FileSystemWatcher? _watcher;

    private System.Threading.Timer? _debounce;
    private bool _refreshing;
    private bool _exited;

    public TrayApplicationContext(TraySettings settings)
    {
        _settings = settings;

        _icon = new TrayIcon();

        _menu = new ContextMenuStrip();

        _openDashboard = new ToolStripMenuItem("Open dashboard");
        _openDashboard.Font = new Font(_openDashboard.Font, FontStyle.Bold);
        _openDashboard.Click += (_, _) => OpenDashboard();

        _refresh = new ToolStripMenuItem("Refresh now");
        _refresh.Click += (_, _) => RefreshNow();

        _planUsage = new ToolStripMenuItem("Log plan usage...");
        _planUsage.Click += (_, _) => LogPlanUsage();

        var openFolder = new ToolStripMenuItem("Open ledger folder");
        openFolder.Click += (_, _) => OpenLedgerFolder();

        _menu.Items.Add(_openDashboard);
        _menu.Items.Add(_refresh);
        _menu.Items.Add(_planUsage);
        _menu.Items.Add(openFolder);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitTray());

        _notify = new NotifyIcon
        {
            Icon = _icon.Current,
            Text = "DevMind",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // Left double-click is the default action, matching the bold default item.
        // NotifyIcon.DoubleClick passes plain EventArgs - there is no Button on it,
        // so the left-button intent is simply assumed (the right button opens the
        // context menu instead).
        _notify.DoubleClick += (_, _) => OpenDashboard();

        UpdateTooltip();

        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();
        _tooltipTimer.Start();

        _watcher = TryStartWatcher();
    }

    // ------------------------------------------------------------------
    // Tooltip
    // ------------------------------------------------------------------

    private void UpdateTooltip()
    {
        try
        {
            _notify.Text = LedgerSummary.BuildTooltip(_settings.LedgerDir, DateTime.Now);
        }
        catch (Exception ex)
        {
            // A torn read of a file being replaced is not worth a balloon.
            _notify.Text = LedgerSummary.Clamp("DevMind: " + ex.Message);
        }
    }

    /// <summary>
    /// Watches daily.csv for the atomic "write .tmp then rename over" the ledger
    //  script performs, plus a Changed event for the case where the rename is
    /// replaced by an in-place write. Debounced so the burst of events from one
    /// run produces a single tooltip refresh.
    /// </summary>
    private FileSystemWatcher? TryStartWatcher()
    {
        try
        {
            if (!Directory.Exists(_settings.LedgerDir))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(_settings.LedgerDir, "daily.csv")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, _) => ScheduleDebouncedTooltip();
            watcher.Created += (_, _) => ScheduleDebouncedTooltip();
            watcher.Deleted += (_, _) => ScheduleDebouncedTooltip();
            watcher.Renamed += (_, _) => ScheduleDebouncedTooltip();
            watcher.Error += (_, _) => ScheduleDebouncedTooltip();

            return watcher;
        }
        catch
        {
            // A watcher is a nice-to-have; the 10-minute timer still covers it.
            return null;
        }
    }

    private void ScheduleDebouncedTooltip()
    {
        // The watcher fires on a thread-pool thread and several times per atomic
        // write; a single one-shot timer collapses that into one refresh.
        var due = _debounce;
        if (due is not null)
        {
            due.Dispose();
        }

        _debounce = new System.Threading.Timer(_ =>
        {
            if (_exited)
            {
                return;
            }

            try
            {
                // NotifyIcon is not a Control, so it has no Invoke/BeginInvoke.
                // The message loop was started with Application.Run(this), whose
                // synchronization context IS the UI thread - post through it.
                var context = SynchronizationContext.Current;
                if (context is not null)
                {
                    context.Post(_ => UpdateTooltip(), null);
                }
                else
                {
                    UpdateTooltip();
                }
            }
            catch
            {
                // The icon can go away between the debounce firing and the
                // marshal; nothing to do about it.
            }
        }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private void OpenDashboard()
    {
        var path = Path.Combine(_settings.LedgerDir, "dashboard.html");
        if (!File.Exists(path))
        {
            Balloon("Dashboard not generated yet - use Refresh now");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Balloon("Could not open dashboard: " + ex.Message);
        }
    }

    private void OpenLedgerFolder()
    {
        if (!Directory.Exists(_settings.LedgerDir))
        {
            Balloon("Ledger folder does not exist: " + _settings.LedgerDir);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _settings.LedgerDir + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Balloon("Could not open ledger folder: " + ex.Message);
        }
    }

    private async void RefreshNow()
    {
        if (_refreshing)
        {
            return;
        }

        SetRefreshing(true);

        // NO ConfigureAwait(false): these are async void UI handlers. The default
        // await resumes on the captured WinForms synchronization context, so the
        // menu/tooltip/balloon work below runs on the UI thread. ConfigureAwait(false)
        // would resume on a thread-pool thread and make every control touch here a
        // cross-thread violation (and an unhandled exception would kill the tray).
        var result = await ScriptRunner.RefreshAsync(_settings.ScriptsDir, _settings.LedgerDir, RefreshTimeout);

        // Back on the UI thread for all menu/tooltip/balloon work.
        SetRefreshing(false);

        if (result.Success)
        {
            UpdateTooltip();
            OpenDashboard();
        }
        else
        {
            UpdateTooltip();
            Balloon(ScriptRunner.DescribeFailure(result, "Refresh failed"));
        }
    }

    private void SetRefreshing(bool refreshing)
    {
        _refreshing = refreshing;
        _refresh.Enabled = !refreshing;
        _refresh.ToolTipText = refreshing ? "Refreshing..." : string.Empty;
    }

    private async void LogPlanUsage()
    {
        using var dialog = new PlanUsageDialog();
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var added = await ScriptRunner.PlanUsageAsync(
            _settings.ScriptsDir,
            _settings.LedgerDir,
            dialog.SessionPct,
            dialog.WeeklyPct,
            dialog.Note);

        if (!added.Success)
        {
            Balloon(ScriptRunner.DescribeFailure(added, "Could not log plan usage"));
            return;
        }

        // The point of logging it is the dashboard section, so refresh right after.
        SetRefreshing(true);
        var refreshed = await ScriptRunner.RefreshAsync(_settings.ScriptsDir, _settings.LedgerDir, RefreshTimeout);
        SetRefreshing(false);

        UpdateTooltip();

        if (refreshed.Success)
        {
            Balloon($"Plan usage logged: session {dialog.SessionPct}% weekly {dialog.WeeklyPct}%");
        }
        else
        {
            Balloon("Plan usage logged, but " + ScriptRunner.DescribeFailure(refreshed, "refresh failed"));
        }
    }

    private void Balloon(string text)
    {
        _notify.ShowBalloonTip(
            8000,
            "DevMind Token Ledger",
            LedgerSummary.Clamp(text),
            ToolTipIcon.Info);
    }

    private void ExitTray()
    {
        _exited = true;
        _debounce?.Dispose();
        _debounce = null;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _debounce?.Dispose();
            _tooltipTimer.Dispose();
            _menu.Dispose();
            _notify.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
