using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

internal static class SvgIconPath
{
    // The first subpath (outer outline) of img/icon.svg with its
    // viewBox transform applied: matrix(1.01601,0,0,1.117483,-1.600954,-13.594109)
    private const string GpuOutline =
        "M11.369,83.536L11.369,19.071L4.994,19.071" +
        "C3.108,19.071 1.576,17.679 1.576,15.963" +
        "C1.576,14.248 3.108,12.855 4.994,12.855" +
        "L18.207,12.855L18.207,26.107L89.266,26.107" +
        "C95.19,26.107 100,30.479 100,35.865L100,63.443" +
        "C100,68.829 95.19,73.202 89.266,73.202L18.207,73.202" +
        "L18.207,83.536C18.207,85.251 16.675,86.644 14.788,86.644" +
        "C12.901,86.644 11.369,85.251 11.369,83.536Z";

    public static GraphicsPath CreateGpu(RectangleF target)
    {
        var tokens = Regex.Matches(GpuOutline, @"[A-Za-z]|[-+]?(?:\d*\.)?\d+")
            .Select(m => m.Value).ToList();

        var path = new GraphicsPath();
        PointF current = default;
        var i = 0;
        while (i < tokens.Count)
        {
            switch (tokens[i++])
            {
                case "M":
                    current = ReadPoint(tokens, ref i);
                    path.StartFigure();
                    break;
                case "L":
                    var lineEnd = ReadPoint(tokens, ref i);
                    path.AddLine(current, lineEnd);
                    current = lineEnd;
                    break;
                case "C":
                    var c1 = ReadPoint(tokens, ref i);
                    var c2 = ReadPoint(tokens, ref i);
                    var curveEnd = ReadPoint(tokens, ref i);
                    path.AddBezier(current, c1, c2, curveEnd);
                    current = curveEnd;
                    break;
            }
        }
        path.CloseFigure();

        var bounds = path.GetBounds();
        var scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);
        using var matrix = new Matrix();
        matrix.Translate(-bounds.X, -bounds.Y);
        matrix.Scale(scale, scale, MatrixOrder.Append);
        matrix.Translate(
            target.X + (target.Width - bounds.Width * scale) / 2f,
            target.Y + (target.Height - bounds.Height * scale) / 2f,
            MatrixOrder.Append);
        path.Transform(matrix);
        return path;
    }

    private static PointF ReadPoint(List<string> tokens, ref int i)
    {
        var x = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        var y = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        return new PointF(1.01601f * x - 1.600954f, 1.117483f * y - 13.594109f);
    }
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
        using var gpu = SvgIconPath.CreateGpu(new RectangleF(0.5f, 0.5f, 31, 31));
        g.FillPath(brush, gpu);
        if (showPercent)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var text = percent.ToString();
            using var font = new Font("Segoe UI", percent == 100 ? 16 : 21, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, textBrush, 16 - size.Width / 2, 16 - size.Height / 2 - 1);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(245, 250, 253));
            var width = (int)Math.Round(22 * percent / 100d);
            g.FillRectangle(fill, 5, 25, width, 3);
        }
        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
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
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 128 || bounds.Height > 128) return false;
        // Reject stale or virtualized rectangles that are not on any monitor.
        var candidate = bounds;
        if (!Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(candidate))) return false;
        return true;
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
