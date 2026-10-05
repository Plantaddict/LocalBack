using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LocalBack.Core.Service;

namespace LocalBack.App.Services;

/// <summary>The notification-area icon. Left click opens the flyout, right click a short menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private Icon? _current;
    private string _currentKey = "";

    public event Action? Clicked;
    public event Action? OpenRequested;
    public event Action? BackUpNowRequested;
    public event Action? RestoreRequested;
    public event Action? PauseToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open LocalBack", null, (_, _) => OpenRequested?.Invoke()).Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add("Back up now", null, (_, _) => BackUpNowRequested?.Invoke());
        menu.Items.Add("Restore…", null, (_, _) => RestoreRequested?.Invoke());
        _pauseItem = new ToolStripMenuItem("Pause for 1 hour", null, (_, _) => PauseToggleRequested?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon { Text = "LocalBack", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Clicked?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        Update(SetHealth.UpToDate, "LocalBack", false);
    }

    /// <summary>Updates the badge colour and tooltip.</summary>
    public void Update(SetHealth worst, string tooltip, bool paused)
    {
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _pauseItem.Text = paused ? "Resume backups" : "Pause for 1 hour";
        Color? badge = worst switch
        {
            SetHealth.Error => Color.FromArgb(0xA1, 0x2A, 0x2A),
            SetHealth.Pending or SetHealth.DriveMissing or SetHealth.Paused => Color.FromArgb(0xB8, 0x5C, 0x00),
            SetHealth.Running => Color.FromArgb(0x0F, 0x5F, 0xBF),
            _ => null,
        };
        var key = badge?.ToArgb().ToString() ?? "none";
        if (key == _currentKey) return;
        _currentKey = key;
        var old = _current;
        _current = Render(badge);
        _icon.Icon = _current;
        if (old != null) DestroyIcon(old.Handle);
    }

    public void ShowBalloon(string title, string text, bool error) =>
        _icon.ShowBalloonTip(5000, title, text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>Draws the glyph from the design at the system's small icon size, with an optional status dot.</summary>
    private static Icon Render(Color? badge)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 24f;
            var accent = Color.FromArgb(0x0F, 0x5F, 0xBF);
            using (var bg = new SolidBrush(accent))
            using (var path = RoundedRect(new RectangleF(0.5f * s, 0.5f * s, 23 * s, 23 * s), 5 * s))
                g.FillPath(bg, path);
            using var pen = new Pen(Color.White, Math.Max(1.4f, 2.2f * s * 0.85f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float k = 0.8f, o = 12 * (1 - k);
            PointF P(float x, float y) => new((o + x * k) * s, (o + y * k) * s);
            using (var monitor = RoundedRect(new RectangleF(P(3, 4).X, P(3, 4).Y, 18 * k * s, 14 * k * s), 2 * k * s))
                g.DrawPath(pen, monitor);
            g.DrawLine(pen, P(7, 20), P(17, 20));
            g.DrawLine(pen, P(12, 8), P(12, 14));
            g.DrawLines(pen, new[] { P(9, 11), P(12, 14), P(15, 11) });
            if (badge is { } c)
            {
                float d = size * 0.46f;
                var r = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, RectangleF.Inflate(r, size * 0.06f, size * 0.06f));
                using var b = new SolidBrush(c);
                g.FillEllipse(b, r);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_current != null) DestroyIcon(_current.Handle);
    }
}
