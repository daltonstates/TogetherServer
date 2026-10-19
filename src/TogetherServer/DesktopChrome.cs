using System.Drawing;

namespace TogetherServer;

internal enum DesktopCaptionGlyph { Minimize, Maximize, Restore, Close }

internal readonly record struct DesktopCaptionStroke(PointF Start, PointF End);

internal static class DesktopChrome
{
    // Keep Windows' minimize/restore, system menu and snap/resize behavior while
    // ChromeForm supplies the visible frame through WM_NCCALCSIZE/hit testing.
    internal const int WindowStyles = 0x00C00000 | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
    internal const int SystemCommandMessage = 0x0112;
    internal const int RestoreCommand = 0xF120;

    internal static float StrokeWidth(int dpi) => 1.5f * dpi / 96f;

    internal static DesktopCaptionStroke[] GlyphStrokes(DesktopCaptionGlyph glyph, Size clientSize, int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        var span = 12f * dpi / 96f;
        var left = (clientSize.Width - span) / 2f;
        var top = (clientSize.Height - span) / 2f;
        var right = left + span;
        var bottom = top + span;
        var middle = top + span / 2f;
        var offset = span / 4f;
        static DesktopCaptionStroke Line(float x1, float y1, float x2, float y2) =>
            new(new(x1, y1), new(x2, y2));
        return glyph switch
        {
            DesktopCaptionGlyph.Minimize => [Line(left, middle, right, middle)],
            DesktopCaptionGlyph.Maximize =>
            [
                Line(left, top, right, top), Line(right, top, right, bottom),
                Line(right, bottom, left, bottom), Line(left, bottom, left, top)
            ],
            DesktopCaptionGlyph.Restore =>
            [
                Line(left + offset, top + offset, left + offset, top),
                Line(left + offset, top, right, top), Line(right, top, right, bottom - offset),
                Line(right, bottom - offset, right - offset, bottom - offset),
                Line(left, top + offset, right - offset, top + offset),
                Line(right - offset, top + offset, right - offset, bottom),
                Line(right - offset, bottom, left, bottom), Line(left, bottom, left, top + offset)
            ],
            DesktopCaptionGlyph.Close => [Line(left, top, right, bottom), Line(right, top, left, bottom)],
            _ => throw new ArgumentOutOfRangeException(nameof(glyph))
        };
    }
}
