using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace GpuMemTray;

internal static class Program
{
    // Held for the whole process lifetime: a second launch detects the held
    // mutex and quits, so two tray icons can never poll the driver at once.
    private static Mutex? singleInstance;
    [STAThread]
    static void Main()
    {
        singleInstance = new Mutex(true, "GpuMemTray_SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication());
    }
}

internal sealed class TrayApplication : ApplicationContext, IMessageFilter
{
    private readonly NotifyIcon trayIcon;
    private readonly PopupWindow popup;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 100 };
    private readonly AppSettings settings = AppSettings.Load();
    private readonly uint taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private bool querying;
    private bool refreshPending;
    private bool exiting;
    private int outsideTicks;
    private int lastIconPercent = -1;
    private bool lastShowPercent;
    private Rectangle lastIconBounds;
    private Point lastShellHover;
    // Starts at 0 (long expired), not long.MinValue: tick - long.MinValue
    // overflows C# long arithmetic to a negative number, which would make
    // HasRecentShellHover permanently true - including a phantom hover zone
    // around (0,0) that could pop the window open in the screen corner.
    private long lastShellHoverStamp;
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
        // Windows signals this before tearing down the session (shutdown,
        // logoff, restart). Polling must stop immediately: a child process
        // spawned now fails to initialize and shows an error dialog.
        SystemEvents.SessionEnding += OnSessionEnding;
        refreshTimer.Start();
        hoverTimer.Start();
        Application.AddMessageFilter(this);
        _ = RefreshAsync();
    }

    public bool PreFilterMessage(ref Message m)
    {
        // Explorer restarted (crash or manual restart): every tray icon is
        // gone and the shell broadcasts TaskbarCreated. Re-register ours,
        // otherwise the app would keep running invisibly.
        if (!exiting && taskbarCreatedMsg != 0 && m.Msg == (int)taskbarCreatedMsg)
        {
            lastIconBounds = default;
            trayIcon.Visible = false;
            trayIcon.Visible = true;
        }
        return false;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        NvidiaSmi.SessionEnding = true;
        exiting = true;
        refreshTimer.Stop();
        hoverTimer.Stop();
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
        var percent = snapshot.Percent;
        // Rebuilding the icon is relatively expensive (HICON creation), so only
        // recreate it when the displayed value actually changed.
        if (percent == lastIconPercent && settings.ShowPercentage == lastShowPercent) return;
        lastIconPercent = percent;
        lastShowPercent = settings.ShowPercentage;
        trayIcon.Icon?.Dispose();
        trayIcon.Icon = TrayIconFactory.Create(percent, settings.ShowPercentage);
    }

    private void ShowPopup()
    {
        if (exiting) return;
        if (!popup.Visible)
        {
            // The timer already refreshes the visible popup every 1.5s, so
            // only rebuild here when it is about to be shown (this also runs
            // on every mouse-move over the icon while hovering).
            popup.SetSnapshot(snapshot);
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
        // The shell can report a stale rectangle on the very first hover after
        // startup (or right after a taskbar re-layout). Since the cursor is what
        // actually hovers the icon whenever this is queried, reject any rectangle
        // that is not near the cursor - otherwise the popup would open far away
        // from the tray icon.
        if (TrayIconBounds.TryGetScreenBounds(trayIcon, out var current) && IsNearCursor(current))
        {
            lastIconBounds = current;
            bounds = current;
        }
        else if (lastIconBounds.Width > 0 && IsNearCursor(lastIconBounds))
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

    private static bool IsNearCursor(Rectangle bounds)
    {
        NativeMethods.GetCursorPos(out var p);
        var zone = bounds;
        zone.Inflate(DpiScaling.Scale(48), DpiScaling.Scale(48));
        return zone.Contains(new Point(p.X, p.Y));
    }

    private void Exit()
    {
        exiting = true;
        refreshTimer.Stop();
        hoverTimer.Stop();
        SystemEvents.SessionEnding -= OnSessionEnding;
        Application.RemoveMessageFilter(this);
        refreshTimer.Dispose();
        hoverTimer.Dispose();
        popup.Dispose();
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
    // False: arrow at the bottom pointing down (bottom taskbar). True: arrow
    // at the top pointing up (top taskbar or popup below the cursor).
    private bool arrowUp;
    private GpuSnapshot current = GpuSnapshot.Empty;

    public PopupWindow()
    {
        DpiScaling.ScaleFactor = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        // The popup is always placed manually next to the tray icon; never let
        // Windows apply its default placement on the first Show().
        StartPosition = FormStartPosition.Manual;
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
        barBackground.Controls.Add(barFill);
        ApplyLayout();
        processes.Size = new Size(388, 0);
    }

    // All inner geometry in one place so a runtime DPI change can rebuild it:
    // positions baked in the constructor would otherwise mix the old factor
    // with the new one (overlapping or gapped rows on high-DPI monitors).
    private void ApplyLayout()
    {
        Padding = DpiScaling.Scale(new Padding(16, 14, 16, 14));

        title.Location = new Point(16, HeaderTop);
        title.Size = new Size(180, TitleRowHeight);

        usage.Location = new Point(200, HeaderTop);
        usage.Size = new Size(204, TitleRowHeight);

        var barTop = HeaderTop + TitleRowHeight + GapTitleToBar;
        barBackground.Location = new Point(16, barTop);
        barBackground.Size = new Size(388, BarHeight);

        var contentTop = barTop + BarHeight + GapBarToContent;
        processes.Location = new Point(16, contentTop);

        empty.Location = new Point(16, contentTop);
        empty.Size = new Size(388, DpiScaling.Scale(60));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        DpiScaling.ScaleFactor = DeviceDpi / 96f;
        ApplyLayout();
        // Rows were built with the old factor; drop them so SetSnapshot
        // recreates them with the new row height.
        foreach (Control row in processes.Controls) row.Dispose();
        processes.Controls.Clear();
        SetSnapshot(current);
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
        // The previous Region is a scarce GDI object: dispose it before
        // replacing, otherwise every show/resize leaks one in this
        // long-running tray app.
        var oldRegion = Region;
        Region = null;
        oldRegion?.Dispose();
        using var path = new GraphicsPath();
        var radius = DpiScaling.Scale(8);
        var bodyHeight = Height - ArrowHeightScaled;
        var bodyTop = arrowUp ? ArrowHeightScaled : 0;
        path.AddArc(0, bodyTop, radius * 2, radius * 2, 180, 90);
        path.AddArc(Width - radius * 2, bodyTop, radius * 2, radius * 2, 270, 90);
        path.AddArc(Width - radius * 2, bodyTop + bodyHeight - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(0, bodyTop + bodyHeight - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        var arrowLeft = arrowX - ArrowWidthScaled / 2;
        if (arrowUp)
        {
            path.AddPolygon(new[]
            {
                new Point(arrowLeft, bodyTop),
                new Point(arrowLeft + ArrowWidthScaled, bodyTop),
                new Point(arrowX, 0)
            });
        }
        else
        {
            var arrowTop = bodyTop + bodyHeight;
            path.AddPolygon(new[]
            {
                new Point(arrowLeft, arrowTop),
                new Point(arrowLeft + ArrowWidthScaled, arrowTop),
                new Point(arrowX, arrowTop + ArrowHeightScaled)
            });
        }
        path.CloseFigure();
        Region = new Region(path);
    }

    public void ShowNear(NativeMethods.POINT cursor, Rectangle iconBounds)
    {
        // DeviceDpi follows the monitor the window is on, so refresh the
        // shared scale factor on every show: a stale factor (cached once in
        // the constructor) would mix scaled and unscaled pixels on a high-DPI
        // monitor and shift the popup by exactly the reported 20-30px.
        if (IsHandleCreated) DpiScaling.ScaleFactor = DeviceDpi / 96f;
        var cursorPoint = new Point(cursor.X, cursor.Y);
        var monitor = Screen.FromPoint(cursorPoint);
        var screen = monitor.WorkingArea;
        var edge = DpiScaling.Scale(6);
        // Horizontal anchor: center on the tray icon itself, not on the
        // cursor. The cursor can sit anywhere over the ~24-40px wide icon, so
        // centering on the cursor shifted the popup (and its arrow) left or
        // right by that amount on every show. The shell icon rect's X center
        // is stable (its top edge is the jittery part on Windows 11, which is
        // why the vertical anchor uses the taskbar rect instead); fall back
        // to the cursor only when no usable rect was reported.
        var anchorX = cursor.X;
        if (iconBounds.Width > 0)
        {
            var iconCenterX = iconBounds.Left + iconBounds.Width / 2;
            if (Math.Abs(iconCenterX - cursor.X) <= DpiScaling.Scale(64))
                anchorX = iconCenterX;
        }
        var x = Math.Clamp(anchorX - Width / 2, screen.Left + edge, Math.Max(screen.Left + edge, screen.Right - Width - edge));
        // Keep the arrow pointing at the icon when the popup is clamped to a
        // screen edge instead of staying centered and pointing at nothing.
        var minArrow = DpiScaling.Scale(16) + ArrowWidthScaled / 2;
        arrowX = Math.Clamp(anchorX - x, minArrow, Math.Max(minArrow, Width - minArrow));

        // Vertical anchoring must be deterministic. The shell-reported icon
        // rectangle jitters by roughly 15-30px on Windows 11 (sometimes the
        // full taskbar-button height, sometimes a stale rect), and the cursor
        // rests somewhere over the icon, typically well below its top edge -
        // so neither is a stable anchor. The taskbar's own rectangle (from
        // SHAppBarMessage, which reports the revealed rect even for an
        // auto-hidden or resized/custom-height taskbar, in the same physical
        // pixels as the cursor) is stable for every show, so the arrow tip
        // always lands just above/below the taskbar.
        int y;
        var hoverTolerance = DpiScaling.Scale(24);
        var hasTaskbar = TaskbarInfo.TryGetRect(out var taskbar, out var taskbarEdge)
            && monitor.Bounds.IntersectsWith(taskbar);
        if (hasTaskbar && taskbarEdge == NativeMethods.AbeBottom && cursor.Y >= taskbar.Top - hoverTolerance)
        {
            // Bottom-docked taskbar (pinned or auto-hidden): park the popup
            // (arrow tip) a few pixels above the taskbar's top edge.
            arrowUp = false;
            y = Math.Max(taskbar.Top - edge - Height, screen.Top + edge);
        }
        else if (hasTaskbar && taskbarEdge == NativeMethods.AbeTop && cursor.Y <= taskbar.Bottom + hoverTolerance)
        {
            // Top-docked taskbar: place the popup just below the taskbar's
            // bottom edge, arrow pointing up at it.
            arrowUp = true;
            y = Math.Min(taskbar.Bottom + edge, screen.Bottom - Height - edge);
        }
        else if (cursor.Y >= screen.Bottom)
        {
            // Bottom taskbar on another monitor whose rect the shell did not
            // report: the working area still marks the taskbar's top edge.
            arrowUp = false;
            y = screen.Bottom - edge - Height;
        }
        else if (cursor.Y < screen.Top)
        {
            arrowUp = true;
            y = screen.Top + edge;
        }
        else
        {
            // Unusual shell layout (e.g. a vertical taskbar): anchor to the
            // cursor rather than the jittery shell icon rect - above it in the
            // lower half so the popup never covers the tray icon, below it in
            // the upper half. The cursor is stable while hovering, the
            // shell rect is not.
            var gap = ArrowGapScaled;
            if (cursor.Y >= screen.Top + screen.Height / 2)
            {
                arrowUp = false;
                y = cursor.Y - Height - gap;
            }
            else
            {
                arrowUp = true;
                y = cursor.Y + gap;
            }
            y = Math.Clamp(y, screen.Top + edge, Math.Max(screen.Top + edge, screen.Bottom - Height - edge));
        }
        // Under PerMonitorV2 the first Show() would create the window handle
        // and reinterpret the requested position through the DPI-adjustment
        // path, offsetting the popup on scaled displays (e.g. 200%). Creating
        // the handle up front makes every Location assignment a direct,
        // unscaled window move, so the very first appearance lands correctly.
        if (!IsHandleCreated) _ = Handle;
        Location = new Point(x, y);
        UpdateRegion();
        Show();
        NativeMethods.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
    }

    public void SetSnapshot(GpuSnapshot data)
    {
        if (IsHandleCreated) DpiScaling.ScaleFactor = DeviceDpi / 96f;
        current = data;
        // The refresh timer calls this every 1.5s even while the popup is
        // visible. A changed process count changes Height - without
        // re-anchoring, the arrow tip would drift by exactly one row height
        // (20-30px scaled), which is the reported intermittent slip.
        var oldHeight = Height;
        var oldLocation = Location;
        var wasVisible = Visible;
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
            var monitor = Screen.FromPoint(new Point(cursor.X, cursor.Y));
            var screen = monitor.WorkingArea;
            // Cap the list to the space actually available above/below the
            // taskbar, not the full working area: with an auto-hidden or
            // resized (custom height) taskbar the working area spans the whole
            // monitor, so the full-height cap would let the popup overlap the
            // revealed taskbar on a high-DPI display.
            var availableHeight = screen.Height;
            if (TaskbarInfo.TryGetRect(out var taskbarRect, out var taskbarEdge)
                && monitor.Bounds.IntersectsWith(taskbarRect))
            {
                if (taskbarEdge == NativeMethods.AbeBottom)
                    availableHeight = taskbarRect.Top - screen.Top;
                else if (taskbarEdge == NativeMethods.AbeTop)
                    availableHeight = screen.Bottom - taskbarRect.Bottom;
            }
            var maxContentHeight = Math.Max(RowHeight,
                availableHeight - HeaderHeight - DpiScaling.Scale(40) - ArrowHeightScaled);

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

        if (wasVisible && IsHandleCreated && Height != oldHeight && !arrowUp)
        {
            // Arrow at the bottom: keep the arrow tip pinned by shifting the
            // top by the height delta so the bottom edge stays where ShowNear
            // put it. (With the arrow at the top, the top edge is already the
            // anchor, so growing downward needs no move.)
            var screen = Screen.FromControl(this).WorkingArea;
            var edge = DpiScaling.Scale(6);
            var newY = oldLocation.Y + (oldHeight - Height);
            Location = new Point(oldLocation.X,
                Math.Clamp(newY, screen.Top + edge, Math.Max(screen.Top + edge, screen.Bottom - Height - edge)));
        }
    }

    private static string FormatGiB(int mib) => mib >= 1024 ? $"{mib / 1024d:0.#} GB" : $"{mib} MB";
    private static Color UsageColor(int p) => p < 60 ? Color.FromArgb(63, 189, 125) : p < 85 ? Color.FromArgb(237, 177, 62) : Color.FromArgb(232, 83, 83);
}

internal sealed class ProcessRow : DoubleBufferedPanel
{
    private string name = string.Empty;
    private string memory = string.Empty;
    private int? pid;
    private bool hoverKill;
    private readonly Font nameFont = new Font("Segoe UI", 9f);
    private readonly Font memoryFont = new Font("Segoe UI Semibold", 9f);
    private readonly Font killFont = new Font("Segoe UI", 10f);
    private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private static readonly StringFormat NameFormat = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
    private static readonly StringFormat MemoryFormat = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
    private Rectangle KillBounds => new(Width - DpiScaling.Scale(32), 0, DpiScaling.Scale(32), Height);
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
        var killLeft = Width - DpiScaling.Scale(32);
        using var nameBrush = new SolidBrush(Color.FromArgb(232, 236, 242));
        e.Graphics.DrawString(name, nameFont, nameBrush, new RectangleF(6, DpiScaling.Scale(4), 246, DpiScaling.Scale(18)), NameFormat);
        using var memoryBrush = new SolidBrush(Color.FromArgb(150, 202, 255));
        e.Graphics.DrawString(memory, memoryFont, memoryBrush, new RectangleF(254, DpiScaling.Scale(4), Math.Max(10, killLeft - 6 - 254), DpiScaling.Scale(18)), MemoryFormat);

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
