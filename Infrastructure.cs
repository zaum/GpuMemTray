using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GpuMemTray;

internal sealed class AppSettings
{
    public bool ShowPercentage { get; set; }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuMemTray", "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

internal static class StartupManager
{
    private const string KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("GpuMemTray") is string;
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            var path = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue("GpuMemTray", $"\"{path}\"");
        }
        else key.DeleteValue("GpuMemTray", false);
    }
}

internal static class DpiScaling
{
    public static float ScaleFactor { get; set; } = 1f;
    public static int Scale(int value) => (int)Math.Round(value * ScaleFactor);
    public static float Scale(float value) => value * ScaleFactor;
    public static Size Scale(Size size) => new(Scale(size.Width), Scale(size.Height));
    public static Point Scale(Point point) => new(Scale(point.X), Scale(point.Y));
    public static Rectangle Scale(Rectangle rect) => new(Scale(rect.X), Scale(rect.Y), Scale(rect.Width), Scale(rect.Height));
    public static Padding Scale(Padding padding) => new(
        Scale(padding.Left), Scale(padding.Top), Scale(padding.Right), Scale(padding.Bottom));
}

internal static class TrayIconFactory
{
    public static Icon Create(int percent, bool showPercent)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var color = percent < 60 ? Color.FromArgb(56, 183, 117) : percent < 85 ? Color.FromArgb(231, 169, 51) : Color.FromArgb(224, 73, 73);
        using var brush = new SolidBrush(color);
        using var border = new Pen(Color.FromArgb(235, 240, 245), 2);
        g.FillRoundedRectangle(brush, new Rectangle(2, 5, 28, 22), 5);
        g.DrawRoundedRectangle(border, new Rectangle(2, 5, 28, 22), 5);
        if (showPercent)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var text = percent.ToString();
            using var font = new Font("Segoe UI", percent == 100 ? 12 : 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, textBrush, 16 - size.Width / 2, 16 - size.Height / 2 - 1);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(245, 250, 253));
            var width = (int)Math.Round(20 * percent / 100d);
            g.FillRectangle(fill, 6, 19 - Math.Min(12, width / 2), width, Math.Min(12, width / 2));
        }
        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedPath(bounds, radius); graphics.FillPath(brush, path);
    }
    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = RoundedPath(bounds, radius); graphics.DrawPath(pen, path);
    }
    private static GraphicsPath RoundedPath(Rectangle b, int r)
    {
        var d = r * 2; var path = new GraphicsPath();
        path.AddArc(b.Left, b.Top, d, d, 180, 90); path.AddArc(b.Right - d, b.Top, d, d, 270, 90);
        path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90); path.AddArc(b.Left, b.Bottom - d, d, d, 90, 90); path.CloseFigure();
        return path;
    }
}

internal static class TrayIconBounds
{
    public static bool TryGetScreenBounds(NotifyIcon icon, out Rectangle bounds)
    {
        bounds = default;
        if (!TryGetIdentifier(icon, out var id)) return false;
        if (NativeMethods.Shell_NotifyIconGetRect(ref id, out var rect) != 0) return false;
        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool TryGetIdentifier(NotifyIcon icon, out NativeMethods.NOTIFYICONIDENTIFIER id)
    {
        id = default;
        var type = typeof(NotifyIcon);
        if (type.GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(icon) is not NativeWindow window
            || window.Handle == IntPtr.Zero)
            return false;

        var rawId = type.GetField("id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(icon);
        if (rawId is not int numericId) return false;

        id = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            hWnd = window.Handle,
            uID = (uint)numericId,
            guidItem = Guid.Empty
        };
        return true;
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);
}
