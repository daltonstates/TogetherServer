using System.Drawing;
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
