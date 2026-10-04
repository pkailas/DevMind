namespace DevMind.TokenLedgerTray;

/// <summary>
/// Small fixed dialog for logging a Claude plan-usage reading. Values map straight
/// onto Add-PlanUsage.ps1's parameters. NumericUpDown clamps to 0..100 so the
/// script's ValidateRange can never reject what is typed here.
/// </summary>
internal sealed class PlanUsageDialog : Form
{
    private readonly NumericUpDown _sessionPct;
    private readonly NumericUpDown _weeklyPct;
    private readonly TextBox _note;

    public int SessionPct => (int)_sessionPct.Value;
    public int WeeklyPct => (int)_weeklyPct.Value;
    public string Note => _note.Text;

    public PlanUsageDialog()
    {
        Text = "Log plan usage";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 172);
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        _sessionPct = NewPercent();
        _weeklyPct = NewPercent();
        _note = new TextBox { Width = 200, Anchor = AnchorStyles.Left | AnchorStyles.Right };

        // Default to the reading the operator is most likely about to type: the
        // session meter usually leads the weekly one.
        _sessionPct.Value = 0;
        _weeklyPct.Value = 0;

        layout.Controls.Add(new Label { Text = "Session %", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_sessionPct, 1, 0);
        layout.Controls.Add(new Label { Text = "Weekly %", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_weeklyPct, 1, 1);
        layout.Controls.Add(new Label { Text = "Note", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_note, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            // FlowDirection (singular) is the property name.
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        // Button has no MinWidth (that is a TableLayoutPanel column concept);
        // an explicit Size is the fixed-dialog equivalent.
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(75, 25),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(75, 25),
        };

        // Added in reverse because the panel flows right to left: OK ends up left
        // of Cancel, which is the Windows order.
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static NumericUpDown NewPercent() => new()
    {
        Minimum = 0,
        Maximum = 100,
        Increment = 1,
        Width = 80,
        Anchor = AnchorStyles.Left,
        TextAlign = HorizontalAlignment.Left,
    };
}
