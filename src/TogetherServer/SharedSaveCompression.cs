using System.IO.Compression;
using System.Net.Http.Headers;

namespace TogetherServer;

// Compression is transport-only: signed manifests, chunk offsets and hashes
// always describe the original bytes. Each decoded request keeps its wire cap.
internal static class SharedSaveCompression
{
    internal const string SeedCapabilityHeader = "X-TogetherServer-Seed-Compression";

    internal static byte[]? Compress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1024) return null;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(bytes);
        return output.Length < bytes.Length ? output.ToArray() : null;
    }

    internal static IResult Chunk(HttpContext context, byte[] bytes)
    {
        context.Response.Headers.Vary = "Accept-Encoding";
        var acceptsGzip = context.Request.GetTypedHeaders().AcceptEncoding?.Any(value =>
            value.Value.Equals("gzip", StringComparison.OrdinalIgnoreCase) && (value.Quality ?? 1) > 0) == true;
        if (acceptsGzip && Compress(bytes) is { } compressed)
        {
            context.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(compressed, "application/octet-stream");
        }
        return Results.Bytes(bytes, "application/octet-stream");
    }

    internal static HttpContent SeedContent(byte[] envelope, bool acceptsGzip)
    {
        var compressed = acceptsGzip ? Compress(envelope) : null;
        var content = new ByteArrayContent(compressed ?? envelope);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (compressed is not null) content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    internal static async Task<byte[]?> ReadPeerRequestAsync(HttpContext context, int maximumBytes)
    {
        var encoding = context.Request.Headers.ContentEncoding;
        if (encoding.Count == 0)
            return await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, maximumBytes, context.RequestAborted);
        if (encoding.Count != 1 || !string.Equals(encoding[0], "gzip", StringComparison.OrdinalIgnoreCase))
            return null;
        var compressed = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
            context.Request.ContentLength, maximumBytes, context.RequestAborted);
        if (compressed is null) return null;
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return await SharedWorldReceiptTrust.ReadBoundedAsync(gzip, null, maximumBytes, context.RequestAborted);
    }
}
