using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace GpuMemTray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication());
    }
}

internal sealed class TrayApplication : ApplicationContext
{
    private const string AppName = "GpuMemTray";
    private readonly NotifyIcon trayIcon;
    private readonly PopupWindow popup;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 100 };
    private readonly AppSettings settings = AppSettings.Load();
    private bool querying;
    private bool refreshPending;
    private bool exiting;
    private int outsideTicks;
    private Rectangle lastIconBounds;
    private Point lastShellHover;
    private long lastShellHoverStamp = long.MinValue;
    private const int ShellHoverGraceMs = 1500;
    private GpuSnapshot snapshot = GpuSnapshot.Empty;

    public TrayApplication()
    {
        popup = new PopupWindow();
        popup.ProcessKilled += () => _ = RefreshAsync();
        trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = string.Empty,
            ContextMenuStrip = BuildMenu(),
            Icon = TrayIconFactory.Create(0, settings.ShowPercentage)
        };
        trayIcon.MouseMove += (_, _) =>
        {
            // The shell only forwards mouse-move messages while the cursor is
            // over the icon, so this point is a reliable anchor even when
            // Shell_NotifyIconGetRect reports an inaccurate rectangle.
            if (NativeMethods.GetCursorPos(out var p))
            {
                lastShellHover = new Point(p.X, p.Y);
                lastShellHoverStamp = Environment.TickCount64;
            }
            ShowPopup();
        };
        trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        hoverTimer.Tick += (_, _) => UpdatePopupHoverState();
        refreshTimer.Start();
        hoverTimer.Start();
        _ = RefreshAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = true };
        var showPercent = new ToolStripMenuItem("Show percentage in icon") { Checked = settings.ShowPercentage, CheckOnClick = true };
        showPercent.CheckedChanged += (_, _) =>
        {
            settings.ShowPercentage = showPercent.Checked;
            settings.Save();
            UpdateTrayIcon();
        };
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupManager.IsEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
        var exit = new ToolStripMenuItem("Quit");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(showPercent);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private async Task RefreshAsync()
    {
        if (querying || exiting)
        {
            refreshPending = true;
            return;
        }
        querying = true;
        refreshPending = false;
        try
        {
            snapshot = await Task.Run(NvidiaSmi.Read);
            popup.SetSnapshot(snapshot);
            trayIcon.Text = string.Empty;
            UpdateTrayIcon();
        }
        catch { /* The popup displays a useful driver-not-found state. */ }
        finally
        {
            querying = false;
            if (refreshPending) _ = RefreshAsync();
        }
    }

    private void UpdateTrayIcon()
    {
        trayIcon.Icon?.Dispose();
        trayIcon.Icon = TrayIconFactory.Create(snapshot.Percent, settings.ShowPercentage);
    }

    private void ShowPopup()
    {
        if (exiting) return;
        popup.SetSnapshot(snapshot);
        if (!popup.Visible)
        {
            NativeMethods.GetCursorPos(out var cursor);
            TryGetIconBounds(out var iconBounds, 0);
            popup.ShowNear(cursor, iconBounds);
        }
    }

    private void UpdatePopupHoverState()
    {
        if (exiting || trayIcon.ContextMenuStrip?.Visible == true) return;

        NativeMethods.GetCursorPos(out var p);
        var cursor = new Point(p.X, p.Y);

        if (popup.Visible)
        {
            if (ContainsCursor(cursor)) { outsideTicks = 0; return; }
            if (++outsideTicks >= 2) { outsideTicks = 0; popup.Hide(); }
            return;
        }

        outsideTicks = 0;
        if (IsOverTrayIcon(cursor)) ShowPopup();
    }

    private bool ContainsCursor(Point screenPoint)
    {
        var padding = DpiScaling.Scale(16);
        var zone = popup.Bounds;
        zone.Inflate(padding, padding);

        if (TryGetIconBounds(out var iconBounds, DpiScaling.Scale(24)))
        {
            zone = Rectangle.Union(zone, iconBounds);
        }

        if (HasRecentShellHover)
        {
            zone = Rectangle.Union(zone, ShellHoverZone());
        }

        return zone.Contains(screenPoint);
    }

    private bool IsOverTrayIcon(Point screenPoint)
    {
        if (TryGetIconBounds(out var iconBounds, DpiScaling.Scale(16)) && iconBounds.Contains(screenPoint))
        {
            return true;
        }

        // Fallback for cases where the shell-queried rectangle is missing or
        // inaccurate (Windows 11 taskbar quirks): trust the last position the
        // shell itself reported as cursor over the icon.
        return HasRecentShellHover && ShellHoverZone().Contains(screenPoint);
    }

    private bool HasRecentShellHover =>
        Environment.TickCount64 - lastShellHoverStamp <= ShellHoverGraceMs;

    private Rectangle ShellHoverZone()
    {
        var radius = DpiScaling.Scale(16);
        return new Rectangle(lastShellHover.X - radius, lastShellHover.Y - radius, radius * 2, radius * 2);
    }

    private bool TryGetIconBounds(out Rectangle bounds, int inflate)
    {
        if (TrayIconBounds.TryGetScreenBounds(trayIcon, out var current))
        {
            lastIconBounds = current;
            bounds = current;
        }
        else if (lastIconBounds.Width > 0)
        {
            bounds = lastIconBounds;
        }
        else
        {
            bounds = default;
            return false;
        }
        bounds.Inflate(inflate, inflate);
        return true;
    }

    private void Exit()
    {
        exiting = true;
        refreshTimer.Stop();
        hoverTimer.Stop();
        popup.Close();
        trayIcon.Visible = false;
        trayIcon.Icon?.Dispose();
        trayIcon.Dispose();
        ExitThread();
    }
}

internal class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        UpdateStyles();
    }
}

internal sealed class PopupWindow : Form
{
    private const int FixedWidth = 420;
    private static int RowHeight => DpiScaling.Scale(26);
    private static int HeaderTop => DpiScaling.Scale(12);
    private static int TitleRowHeight => DpiScaling.Scale(22);
    private static int GapTitleToBar => DpiScaling.Scale(6);
    private static int BarHeight => DpiScaling.Scale(6);
    private static int GapBarToContent => DpiScaling.Scale(8);
    private static int HeaderHeight => HeaderTop + TitleRowHeight + GapTitleToBar + BarHeight + GapBarToContent;
    private const int ArrowWidth = 20;
    private const int ArrowHeight = 10;
    private const int ArrowGap = 40;
    private static int ArrowWidthScaled => DpiScaling.Scale(ArrowWidth);
    private static int ArrowHeightScaled => DpiScaling.Scale(ArrowHeight);
    private static int ArrowGapScaled => DpiScaling.Scale(ArrowGap);

    private readonly Label title = new() { AutoSize = false, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label usage = new() { AutoSize = false, Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(210, 218, 230), TextAlign = ContentAlignment.MiddleRight };
    private readonly Panel barBackground = new() { BackColor = Color.FromArgb(53, 60, 72) };
    private readonly Panel barFill = new();
    private readonly DoubleBufferedPanel processes = new() { AutoScroll = false, BackColor = Color.Transparent };
    private readonly Label empty = new() { AutoSize = false, ForeColor = Color.FromArgb(167, 177, 191), Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleCenter };
    public event Action? ProcessKilled;
    private int arrowX;

    public PopupWindow()
    {
        DpiScaling.ScaleFactor = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Width = FixedWidth;
        Height = DpiScaling.Scale(120);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 32, 39);
        Padding = DpiScaling.Scale(new Padding(16, 14, 16, 14));

        Controls.AddRange([title, usage, barBackground, processes, empty]);

        title.Location = new Point(16, HeaderTop);
        title.Size = new Size(180, TitleRowHeight);

        usage.Location = new Point(200, HeaderTop);
        usage.Size = new Size(204, TitleRowHeight);

        var barTop = HeaderTop + TitleRowHeight + GapTitleToBar;
        barBackground.Location = new Point(16, barTop);
        barBackground.Size = new Size(388, BarHeight);
        barBackground.Controls.Add(barFill);

        var contentTop = barTop + BarHeight + GapBarToContent;
        processes.Location = new Point(16, contentTop);
        processes.Size = new Size(388, 0);

        empty.Location = new Point(16, contentTop);
        empty.Size = new Size(388, DpiScaling.Scale(60));

    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        var path = new GraphicsPath();
        var radius = DpiScaling.Scale(8);
        var bodyHeight = Height - ArrowHeightScaled;
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        path.AddArc(Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        path.AddArc(Width - radius * 2, bodyHeight - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(0, bodyHeight - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        var arrowLeft = arrowX - ArrowWidthScaled / 2;
        var arrowTop = bodyHeight;
        path.AddPolygon(new[]
        {
            new Point(arrowLeft, arrowTop),
            new Point(arrowLeft + ArrowWidthScaled, arrowTop),
            new Point(arrowX, arrowTop + ArrowHeightScaled)
        });
        path.CloseFigure();
        Region = new Region(path);
    }

    public void ShowNear(NativeMethods.POINT cursor, Rectangle iconBounds)
    {
        var screen = Screen.FromPoint(new Point(cursor.X, cursor.Y)).WorkingArea;
        // The arrow is always centered in the popup. The popup is centered on the cursor
        // (which is over the tray icon when the popup appears).
        var x = Math.Clamp(cursor.X - Width / 2, screen.Left + DpiScaling.Scale(6), screen.Right - Width - DpiScaling.Scale(6));
        arrowX = Width / 2;
        var y = iconBounds.Height > 0
            ? iconBounds.Top - Height - ArrowGapScaled
            : screen.Bottom - Height - ArrowGapScaled;
        if (y < screen.Top + DpiScaling.Scale(6)) y = screen.Top + DpiScaling.Scale(6);
        Location = new Point(x, y);
        UpdateRegion();
        Show();
        NativeMethods.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
    }

    public void SetSnapshot(GpuSnapshot data)
    {
        SuspendLayout();
        processes.SuspendLayout();

        var titleText = data.Available ? "GPU MEMORY" : "GPU MEMORY — UNAVAILABLE";
        if (title.Text != titleText) title.Text = titleText;

        // When the total VRAM is unknown (TotalMiB == 0), show only the used
        // amount instead of a misleading "X / 0 MB".
        var usageText = data.Available
            ? data.TotalMiB > 0
                ? $"{FormatGiB(data.UsedMiB)} / {FormatGiB(data.TotalMiB)}"
                : $"{FormatGiB(data.UsedMiB)} used"
            : "nvidia-smi unavailable";
        if (usage.Text != usageText) usage.Text = usageText;

        var newBarWidth = (int)Math.Round(barBackground.Width * data.Percent / 100.0);
        if (barFill.Width != newBarWidth) barFill.Width = newBarWidth;
        barFill.Height = barBackground.Height;

        var usageColor = UsageColor(data.Percent);
        if (barFill.BackColor != usageColor) barFill.BackColor = usageColor;

        if (!data.Available || data.Processes.Count == 0)
        {
            processes.Visible = false;
            empty.Visible = true;
            var emptyText = data.Available
                ? "No process data is available from the GPU driver."
                : "The NVIDIA driver tool did not respond.\nCheck whether nvidia-smi runs from Command Prompt.";
            if (empty.Text != emptyText) empty.Text = emptyText;
            Height = HeaderHeight + empty.Height + DpiScaling.Scale(14) + ArrowHeightScaled;
        }
        else
        {
            empty.Visible = false;
            processes.Visible = true;

            var count = data.Processes.Count;
            var totalProcessHeight = count * RowHeight;

            NativeMethods.GetCursorPos(out var cursor);
            var screen = Screen.FromPoint(new Point(cursor.X, cursor.Y)).WorkingArea;
            var maxContentHeight = screen.Height - HeaderHeight - DpiScaling.Scale(40) - ArrowHeightScaled;

            if (totalProcessHeight > maxContentHeight)
            {
                processes.AutoScroll = true;
                processes.Size = new Size(388, maxContentHeight);
                Height = HeaderHeight + maxContentHeight + DpiScaling.Scale(14) + ArrowHeightScaled;
            }
            else
            {
                processes.AutoScroll = false;
                processes.Size = new Size(388, totalProcessHeight);
                Height = HeaderHeight + totalProcessHeight + DpiScaling.Scale(14) + ArrowHeightScaled;
            }

            // Remove extra rows if count decreased
            while (processes.Controls.Count > count)
            {
                var idx = processes.Controls.Count - 1;
                var ctrl = processes.Controls[idx];
                processes.Controls.RemoveAt(idx);
                ctrl.Dispose();
            }

            // Update or add rows
            for (int i = 0; i < count; i++)
            {
                if (i < processes.Controls.Count && processes.Controls[i] is ProcessRow existingRow)
                {
                    existingRow.Location = new Point(0, i * RowHeight);
                    existingRow.Width = processes.ClientSize.Width;
                    existingRow.UpdateData(data.Processes[i]);
                }
                else
                {
                    var newRow = new ProcessRow(data.Processes[i])
                    {
                        Location = new Point(0, i * RowHeight),
                        Width = processes.ClientSize.Width
                    };
                    newRow.ProcessKilled += () => ProcessKilled?.Invoke();
                    processes.Controls.Add(newRow);
                }
            }
        }

        processes.ResumeLayout(true);
        ResumeLayout(true);
    }

    private static string FormatGiB(int mib) => mib >= 1024 ? $"{mib / 1024d:0.#} GB" : $"{mib} MB";
    private static Color UsageColor(int p) => p < 60 ? Color.FromArgb(63, 189, 125) : p < 85 ? Color.FromArgb(237, 177, 62) : Color.FromArgb(232, 83, 83);
}

internal sealed class ProcessRow : DoubleBufferedPanel
{
    private string name;
    private string memory;
    private int? pid;
    private bool hoverKill;
    private readonly Font nameFont = new Font("Segoe UI", 9f);
    private readonly Font memoryFont = new Font("Segoe UI Semibold", 9f);
    private readonly Font killFont = new Font("Segoe UI", 10f);
    private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private Rectangle KillBounds => new(Width - 32, 0, 32, Height);
    public event Action? ProcessKilled;

    public ProcessRow(GpuProcess process)
    {
        Size = new Size(388, DpiScaling.Scale(26));
        Margin = Padding.Empty;
        UpdateData(process);
        Paint += OnPaint;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseClick += OnMouseClick;
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var nameBrush = new SolidBrush(Color.FromArgb(232, 236, 242));
        e.Graphics.DrawString(name, nameFont, nameBrush, new RectangleF(6, DpiScaling.Scale(4), 246, DpiScaling.Scale(18)), new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
        using var memoryBrush = new SolidBrush(Color.FromArgb(150, 202, 255));
        e.Graphics.DrawString(memory, memoryFont, memoryBrush, new RectangleF(254, DpiScaling.Scale(4), 94, DpiScaling.Scale(18)), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });

        if (pid is null) return;

        var bounds = KillBounds;
        if (hoverKill)
        {
            using var hoverBg = new SolidBrush(Color.FromArgb(232, 83, 83));
            e.Graphics.FillRectangle(hoverBg, bounds);
        }
        using var killBrush = new SolidBrush(hoverKill ? Color.White : Color.FromArgb(150, 160, 175));
        e.Graphics.DrawString("✕", killFont, killBrush, bounds, CenterFormat);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        var overKill = pid is not null && KillBounds.Contains(e.Location);
        if (hoverKill != overKill)
        {
            hoverKill = overKill;
            Invalidate();
        }
    }

    private void OnMouseLeave(object? sender, EventArgs e)
    {
        if (hoverKill)
        {
            hoverKill = false;
            Invalidate();
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pid is int p && KillBounds.Contains(e.Location))
        {
            TryKill(p);
            ProcessKilled?.Invoke();
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch { /* Process already exited or access is denied. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            nameFont.Dispose();
            memoryFont.Dispose();
            killFont.Dispose();
        }
        base.Dispose(disposing);
    }

    public void UpdateData(GpuProcess process)
    {
        name = process.Name;
        memory = FormatMemory(process.MemoryMiB);
        pid = process.Pid;
        Invalidate();
    }

    private static string FormatMemory(int? memoryMiB) =>
        memoryMiB is int m ? (m >= 1024 ? $"{m / 1024d:0.#} GB" : $"{m} MB") : "—";
}
