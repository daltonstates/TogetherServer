using System.Drawing;

namespace TogetherServer;

internal static class DesktopIcon
{
    // Load the same multi-size asset used by the executable, rather than a
    // temporary 32-pixel drawing. Icon retains the image data after stream disposal.
    internal static Icon Load(bool staging, Size size)
    {
        using var stream = OpenResource(staging);
        return new Icon(stream, size);
    }

    // Source checks inspect these bytes without constructing an Icon, Form or HWND.
    internal static Stream OpenResource(bool staging) => typeof(DesktopIcon).Assembly.GetManifestResourceStream(
        staging ? "TogetherServer.Icons.Development.ico" : "TogetherServer.Icons.Production.ico")
        ?? throw new InvalidOperationException("The bundled TogetherServer icon is missing.");
}
