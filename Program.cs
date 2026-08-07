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
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 180 };
    private readonly AppSettings settings = AppSettings.Load();
    private bool querying;
    private bool exiting;
    private GpuSnapshot snapshot = GpuSnapshot.Empty;

    public TrayApplication()
    {
        popup = new PopupWindow();
        trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = string.Empty,
            ContextMenuStrip = BuildMenu(),
            Icon = TrayIconFactory.Create(0, settings.ShowPercentage)
        };
        trayIcon.MouseMove += (_, _) => ShowPopup();
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
        if (querying || exiting) return;
        querying = true;
        try
        {
            snapshot = await Task.Run(NvidiaSmi.Read);
            popup.SetSnapshot(snapshot);
            trayIcon.Text = string.Empty;
            UpdateTrayIcon();
        }
        catch { /* The popup displays a useful driver-not-found state. */ }
        finally { querying = false; }
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
            popup.ShowNear(cursor);
        }
    }

    private void UpdatePopupHoverState()
    {
        if (exiting || trayIcon.ContextMenuStrip?.Visible == true) return;

        NativeMethods.GetCursorPos(out var p);
        var cursor = new Point(p.X, p.Y);

        if (popup.Visible)
        {
            if (!ContainsCursor(cursor)) popup.Hide();
            return;
        }

        if (IsOverTrayIcon(cursor)) ShowPopup();
    }

    private bool ContainsCursor(Point screenPoint)
    {
        var padding = DpiScaling.Scale(16);
        var zone = popup.Bounds;
        zone.Inflate(padding, padding);

        if (TrayIconBounds.TryGetScreenBounds(trayIcon, out var iconBounds))
        {
            iconBounds.Inflate(padding, padding);
            zone = Rectangle.Union(zone, iconBounds);
        }

        return zone.Contains(screenPoint);
    }

    private bool IsOverTrayIcon(Point screenPoint)
    {
        if (!TrayIconBounds.TryGetScreenBounds(trayIcon, out var iconBounds)) return false;
        iconBounds.Inflate(DpiScaling.Scale(8), DpiScaling.Scale(8));
        return iconBounds.Contains(screenPoint);
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

    private readonly Label title = new() { AutoSize = false, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label usage = new() { AutoSize = false, Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(210, 218, 230), TextAlign = ContentAlignment.MiddleRight };
    private readonly Panel barBackground = new() { BackColor = Color.FromArgb(53, 60, 72) };
    private readonly Panel barFill = new();
    private readonly DoubleBufferedPanel processes = new() { AutoScroll = false, BackColor = Color.Transparent };
    private readonly Label empty = new() { AutoSize = false, ForeColor = Color.FromArgb(167, 177, 191), Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleCenter };

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
        var path = new GraphicsPath();
        var radius = DpiScaling.Scale(8);
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        path.AddArc(Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        path.AddArc(Width - radius * 2, Height - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(0, Height - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    public void ShowNear(NativeMethods.POINT cursor)
    {
        var screen = Screen.FromPoint(new Point(cursor.X, cursor.Y)).WorkingArea;
        var x = Math.Clamp(cursor.X - Width + DpiScaling.Scale(24), screen.Left + DpiScaling.Scale(6), screen.Right - Width - DpiScaling.Scale(6));
        var y = cursor.Y - Height - DpiScaling.Scale(10);
        if (y < screen.Top + DpiScaling.Scale(6)) y = cursor.Y + DpiScaling.Scale(10);
        Location = new Point(x, y);
        Show();
        NativeMethods.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
    }

    public void SetSnapshot(GpuSnapshot data)
    {
        SuspendLayout();
        processes.SuspendLayout();

        var titleText = data.Available ? "GPU MEMORY" : "GPU MEMORY — UNAVAILABLE";
        if (title.Text != titleText) title.Text = titleText;

        var usageText = data.Available ? $"{FormatGiB(data.UsedMiB)} / {FormatGiB(data.TotalMiB)}" : "nvidia-smi unavailable";
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
            Height = HeaderHeight + empty.Height + DpiScaling.Scale(14);
        }
        else
        {
            empty.Visible = false;
            processes.Visible = true;

            var count = data.Processes.Count;
            var totalProcessHeight = count * RowHeight;

            NativeMethods.GetCursorPos(out var cursor);
            var screen = Screen.FromPoint(new Point(cursor.X, cursor.Y)).WorkingArea;
            var maxContentHeight = screen.Height - HeaderHeight - DpiScaling.Scale(40);

            if (totalProcessHeight > maxContentHeight)
            {
                processes.AutoScroll = true;
                processes.Size = new Size(388, maxContentHeight);
                Height = HeaderHeight + maxContentHeight + DpiScaling.Scale(14);
            }
            else
            {
                processes.AutoScroll = false;
                processes.Size = new Size(388, totalProcessHeight);
                Height = HeaderHeight + totalProcessHeight + DpiScaling.Scale(14);
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
    private readonly Font nameFont = new Font("Segoe UI", 9f);
    private readonly Font memoryFont = new Font("Segoe UI Semibold", 9f);

    public ProcessRow(GpuProcess process)
    {
        Size = new Size(388, DpiScaling.Scale(26));
        Margin = Padding.Empty;
        name = process.Name;
        memory = FormatMemory(process.MemoryMiB);
        Paint += OnPaint;
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        e.Graphics.DrawString(name, nameFont, new SolidBrush(Color.FromArgb(232, 236, 242)), new RectangleF(6, DpiScaling.Scale(4), 286, DpiScaling.Scale(18)), new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
        e.Graphics.DrawString(memory, memoryFont, new SolidBrush(Color.FromArgb(150, 202, 255)), new RectangleF(294, DpiScaling.Scale(4), 88, DpiScaling.Scale(18)), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
    }

    public void UpdateData(GpuProcess process)
    {
        name = process.Name;
        memory = FormatMemory(process.MemoryMiB);
        Invalidate();
    }

    private static string FormatMemory(int? memoryMiB) =>
        memoryMiB is int m ? (m >= 1024 ? $"{m / 1024d:0.#} GB" : $"{m} MB") : "—";
}