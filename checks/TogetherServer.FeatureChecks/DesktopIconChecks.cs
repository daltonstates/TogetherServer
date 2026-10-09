using System.Buffers.Binary;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class DesktopIconChecks
{
    // Read only embedded bytes. Never construct Icon/Form, create a native handle,
    // launch the app, or query the desktop from the active-desktop source runner.
    internal static void Run()
    {
        var production = Read(false);
        var development = Read(true);
        Validate(production, false);
        Validate(development, true);
        if (production.AsSpan().SequenceEqual(development))
            throw new Exception("Development lost its distinct square D icon.");
    }

    private static byte[] Read(bool staging)
    {
        using var resource = DesktopIcon.OpenResource(staging);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Validate(byte[] bytes, bool staging)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        if (bytes.Length is < 166 or > 1_048_576 || U16(bytes, 0) != 0 ||
            U16(bytes, 2) != 1 || U16(bytes, 4) != sizes.Length)
            throw new Exception("The embedded icon has an invalid or incomplete ICO directory.");

        var nextOffset = 6 + sizes.Length * 16;
        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            var entry = 6 + index * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var length = I32(bytes, entry + 8);
            var offset = I32(bytes, entry + 12);
            var maskStride = (size + 31) / 32 * 4;
            var expectedLength = 40 + size * size * 4 + maskStride * size;
            if (width != size || height != size || bytes[entry + 2] != 0 || bytes[entry + 3] != 0 ||
                U16(bytes, entry + 4) != 1 || U16(bytes, entry + 6) != 32 ||
                length != expectedLength || offset != nextOffset || offset > bytes.Length - length)
                throw new Exception($"The embedded {size}px icon frame is invalid or missing.");
            nextOffset += length;
            if (I32(bytes, offset) != 40 || I32(bytes, offset + 4) != size ||
                I32(bytes, offset + 8) != size * 2 || U16(bytes, offset + 12) != 1 ||
                U16(bytes, offset + 14) != 32 || I32(bytes, offset + 16) != 0)
                throw new Exception($"The embedded {size}px icon is not a 32-bit DIB.");

            var orange = 0;
            var ink = 0;
            for (var pixel = 0; pixel < size * size; pixel++)
            {
                var at = offset + 40 + pixel * 4;
                var b = bytes[at];
                var g = bytes[at + 1];
                var r = bytes[at + 2];
                var a = bytes[at + 3];
                if (a >= 230 && r >= 240 && g is >= 125 and <= 150 && b <= 45) orange++;
                if (a >= 230 && r < 100 && g < 80 && b < 60) ink++;
                var row = pixel / size;
                var column = pixel % size;
                var mask = offset + 40 + size * size * 4 + row * maskStride + column / 8;
                var masked = (bytes[mask] & (1 << (7 - column % 8))) != 0;
                if (masked != (a == 0))
                    throw new Exception($"The embedded {size}px alpha and legacy transparency mask disagree.");
            }
            if (orange < size * size / 3 || ink < size * size / 30)
                throw new Exception($"The embedded {size}px icon is blank or lost its orange/black glyph.");

            // The D tile's inset is only 0.8px at 16px. Antialiasing preserves
            // partial corner coverage rather than producing an exactly-zero
            // pixel. Every corner must still be at least 75% transparent.
            foreach (var corner in new[] { 0, size - 1, (size - 1) * size, size * size - 1 })
                if (bytes[offset + 40 + corner * 4 + 3] > 64)
                    throw new Exception($"The embedded {size}px icon lost its transparent tile corners.");

            var center = offset + 40 + ((size - 1 - size / 2) * size + size / 2) * 4;
            if (bytes[center + 3] < 230 || (staging ? bytes[center + 2] < 240 : bytes[center + 2] >= 100))
                throw new Exception($"The embedded {size}px icon lost its {(staging ? "D" : "T")} identity.");
        }
        if (nextOffset != bytes.Length)
            throw new Exception("The icon contains bytes outside its reviewed frame directory.");
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
}
