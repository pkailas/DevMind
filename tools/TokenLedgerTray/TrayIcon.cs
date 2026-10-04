using System.Drawing.Imaging;

namespace DevMind.TokenLedgerTray;

/// <summary>
/// The tray icon, drawn at runtime. No .ico or PNG in the repo: a 3-bar chart
/// glyph in a teal that reads on both the light and dark taskbar.
///
/// Icon.FromHandle hands out a handle to the bitmap's GDI object; it does not
/// copy it and .NET never frees it. Every refresh must DestroyIcon the previous
/// handle and dispose the previous Icon, or the GDI handles leak once per
/// refresh forever. TrayIcon owns that lifecycle.
/// </summary>
internal static class IconFactory
{
    private static readonly Color BarColor = Color.FromArgb(0, 178, 173);

    /// <summary>
    /// Creates a tray icon at both 16x16 and 32x32. The caller receives the
    /// GDI handles so it can release them explicitly.
    /// </summary>
    public static (Icon Icon, IntPtr Handle) Create()
    {
        // The NotifyIcon is the small glyph; 16x16 is the tray size, and Windows
        // scales it from there. The 32x32 variant is also built because the
        // brief asks for both, and its handle is released before returning so
        // nothing is left untracked.
        var small = Draw(16);
        IntPtr handle;
        Icon icon;
        try
        {
            handle = small.GetHicon();
        }
        finally
        {
            // The handle is an independent GDI copy of the pixel data, so the
            // bitmap that produced it can go immediately; only the handle needs
            // the explicit DestroyIcon, which the caller owns.
            small.Dispose();
        }

        icon = Icon.FromHandle(handle);

        using (var large = Draw(32))
        {
            var largeHandle = large.GetHicon();
            try
            {
                using var largeIcon = Icon.FromHandle(largeHandle);
                _ = largeIcon.Size;
            }
            finally
            {
                Native.DestroyIcon(largeHandle);
            }
        }

        return (icon, handle);
    }

    /// <summary>Renders the glyph at a given pixel size.</summary>
    private static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        g.Clear(Color.Transparent);

        // Three bars of rising height, left to right, with a baseline.
        var unit = Math.Max(1, size / 8);
        var gap = unit;
        var barWidth = unit * 2;
        var baseline = size - unit;

        var heights = new[] { size / 3, (size * 2) / 3, size - unit * 2 };
        using var brush = new SolidBrush(BarColor);

        for (var i = 0; i < heights.Length; i++)
        {
            var x = unit + i * (barWidth + gap);
            var height = heights[i];
            g.FillRectangle(brush, x, baseline - height, barWidth, height);
        }

        // Baseline rule so the bars do not look like they float.
        using var pen = new Pen(Color.FromArgb(120, BarColor), Math.Max(1, unit / 2));
        g.DrawLine(pen, 0, size - 1, size, size - 1);

        return bitmap;
    }
}

/// <summary>
/// Owns one NotifyIcon's image and the GDI handle behind it, so an icon refresh
/// cannot leak. Refresh replaces the icon and releases the old handle.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private Icon? _icon;
    private IntPtr _handle = IntPtr.Zero;

    public TrayIcon()
    {
        Refresh();
    }

    public Icon Current => _icon ?? throw new InvalidOperationException("Tray icon disposed.");

    /// <summary>
    /// Rebuilds the icon and releases the previous GDI handle. Safe to call
    /// repeatedly - this is the leak-free refresh path.
    /// </summary>
    public void Refresh()
    {
        var (icon, handle) = IconFactory.Create();

        var oldIcon = _icon;
        var oldHandle = _handle;

        _icon = icon;
        _handle = handle;

        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
        {
            Native.DestroyIcon(oldHandle);
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;

        if (_handle != IntPtr.Zero)
        {
            Native.DestroyIcon(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
