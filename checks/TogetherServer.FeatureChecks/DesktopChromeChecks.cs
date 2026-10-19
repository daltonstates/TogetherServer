using System.Drawing;
using System.Runtime.InteropServices;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class DesktopChromeChecks
{
    // Pure geometry/style evidence only. No Form, native handle, desktop query,
    // app launch or simulated input is allowed in this source runner.
    internal static void Run()
    {
        const int caption = 0x00C00000;
        const int systemMenu = 0x00080000;
        const int sizingBorder = 0x00040000;
        const int minimize = 0x00020000;
        const int maximize = 0x00010000;
        Require((DesktopChrome.WindowStyles & (caption | systemMenu | sizingBorder | minimize | maximize)) ==
                (caption | systemMenu | sizingBorder | minimize | maximize),
            "Custom chrome lost a Windows caption/system/minimize/maximize/resize style.");

        foreach (var dpi in new[] { 96, 120, 144, 192, 288 })
        foreach (var logicalSize in new[] { new Size(46, 42), new Size(52, 48) })
        {
            var scale = dpi / 96f;
            var size = new Size((int)(logicalSize.Width * scale), (int)(logicalSize.Height * scale));
            var bounds = Enum.GetValues<DesktopCaptionGlyph>().ToDictionary(glyph => glyph,
                glyph => Bounds(DesktopChrome.GlyphStrokes(glyph, size, dpi)));
            foreach (var (glyph, rectangle) in bounds)
            {
                Require(Near(rectangle.Width, 12 * scale), $"{glyph} lost the shared 12-DIP width at {dpi} DPI.");
                Require(Near(rectangle.Left + rectangle.Width / 2, size.Width / 2f) &&
                        Near(rectangle.Top + rectangle.Height / 2, size.Height / 2f),
                    $"{glyph} was not centered at {dpi} DPI.");
                Require(rectangle.Left >= DesktopChrome.StrokeWidth(dpi) &&
                        rectangle.Right <= size.Width - DesktopChrome.StrokeWidth(dpi),
                    $"{glyph} would clip its stroke at {dpi} DPI.");
            }
            Require(bounds[DesktopCaptionGlyph.Close] == bounds[DesktopCaptionGlyph.Maximize] &&
                    bounds[DesktopCaptionGlyph.Close] == bounds[DesktopCaptionGlyph.Restore],
                $"Close, maximize and restore differ in apparent extent at {dpi} DPI.");
            var close = DesktopChrome.GlyphStrokes(DesktopCaptionGlyph.Close, size, dpi);
            Require(close.Length == 2 && close.All(stroke =>
                    Near(Math.Abs(stroke.End.X - stroke.Start.X), Math.Abs(stroke.End.Y - stroke.Start.Y))) &&
                    Math.Sign(close[0].End.X - close[0].Start.X) != Math.Sign(close[1].End.X - close[1].Start.X),
                "The close glyph no longer forms two complete crossing diagonals.");
            Require(Near(DesktopChrome.StrokeWidth(dpi), 1.5f * scale), "Caption stroke weight failed DPI scaling.");
        }

        var rightMonitor = new Rectangle(1920, -1080, 1920, 1080);
        var workingArea = new Rectangle(1960, -1040, 1880, 1040);
        Require(DesktopWindow.RelativeMaximizedBounds(rightMonitor, workingArea) == new Rectangle(40, 40, 1880, 1040),
            "Custom frame maximization lost the selected monitor or taskbar insets.");
        CheckMaximizedWorkArea();
    }

    private static void CheckMaximizedWorkArea()
    {
        Require(Marshal.SizeOf<DesktopMinMaxInfo>() == 40 &&
                Marshal.OffsetOf<DesktopMinMaxInfo>("MaxSize").ToInt32() == 8 &&
                Marshal.OffsetOf<DesktopMinMaxInfo>("MaxPosition").ToInt32() == 16 &&
                Marshal.OffsetOf<DesktopMinMaxInfo>("MinTrackSize").ToInt32() == 24 &&
                Marshal.OffsetOf<DesktopMinMaxInfo>("MaxTrackSize").ToInt32() == 32,
            "The native MINMAXINFO layout no longer matches five Win32 POINT values.");

        foreach (var monitor in new[] { new Rectangle(0, 0, 1024, 768), new Rectangle(-1920, 0, 1920, 1080),
                     new Rectangle(1920, -1080, 1920, 1080) })
        foreach (var insets in new[] { new Rectangle(0, 0, 0, 48), new Rectangle(40, 0, 0, 0),
                     new Rectangle(0, 40, 0, 0), new Rectangle(0, 0, 40, 0), new Rectangle(40, 40, 0, 0) })
        {
            var work = new Rectangle(monitor.Left + insets.X, monitor.Top + insets.Y,
                monitor.Width - insets.X - insets.Width, monitor.Height - insets.Y - insets.Height);
            var info = new DesktopMinMaxInfo
            {
                Reserved = new(17, 23), MaxPosition = new(-8, -8), MaxSize = new(monitor.Width + 16, monitor.Height + 16),
                MinTrackSize = new(380, 560), MaxTrackSize = new(8000, 4000)
            };
            Require(DesktopChrome.ApplyMaximizedWorkArea(ref info, monitor, work), "A valid monitor work area was rejected.");
            Require(info.MaxPosition == new Point(insets.X, insets.Y) && info.MaxSize == new Point(work.Width, work.Height),
                "The native maximized frame retained invisible border inflation or lost monitor-relative taskbar insets.");
            Require(info.Reserved == new Point(17, 23) && info.MinTrackSize == new Point(380, 560) &&
                    info.MaxTrackSize == new Point(8000, 4000),
                "The work-area override changed WinForms' reserved or track constraints.");
        }
        var unchanged = new DesktopMinMaxInfo { MaxPosition = new(11, 12), MaxSize = new(300, 200) };
        foreach (var invalid in new[] { Rectangle.Empty, new Rectangle(0, 0, -1, 40), new Rectangle(2000, 0, 100, 100) })
            Require(!DesktopChrome.ApplyMaximizedWorkArea(ref unchanged, new(0, 0, 1024, 768), invalid) &&
                    unchanged.MaxPosition == new Point(11, 12) && unchanged.MaxSize == new Point(300, 200),
                "Invalid work-area dimensions changed the native maximum rectangle.");
    }

    private static RectangleF Bounds(DesktopCaptionStroke[] strokes)
    {
        var points = strokes.SelectMany(stroke => new[] { stroke.Start, stroke.End }).ToArray();
        return RectangleF.FromLTRB(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
    }

    private static bool Near(float first, float second) => Math.Abs(first - second) < .001f;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
