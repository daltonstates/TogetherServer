using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class SharedSaveCompressionChecks
{
    internal static async Task RunAsync(string root)
    {
        await SlowSaveTransferChecks.RunAsync(root);
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var payload = Enumerable.Repeat((byte)'s', SharedWorldService.ChunkBytes).ToArray();
        var compressed = SharedSaveCompression.Compress(payload)!;
        Require(compressed.Length < payload.Length / 10, "Compressible save data was not reduced.");
        Require(SharedSaveCompression.Compress(new byte[100]) is null &&
            SharedSaveCompression.Compress(RandomNumberGenerator.GetBytes(payload.Length)) is null,
            "Tiny or incompressible chunks grew on the wire.");
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        foreach (var accepts in new[] { "gzip", "gzip;q=0", "" })
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers.AcceptEncoding = accepts;
            context.Response.Body = new MemoryStream();
            await SharedSaveCompression.Chunk(context, payload).ExecuteAsync(context);
            var wire = ((MemoryStream)context.Response.Body).ToArray();
            Require((context.Response.Headers.ContentEncoding == "gzip") == (accepts == "gzip"),
                "Compression ignored explicit negotiation or compressed an old client response.");
            if (accepts == "gzip")
            {
                using var source = new MemoryStream(wire);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                await gzip.CopyToAsync(decoded);
                wire = decoded.ToArray();
            }
            Require(SHA256.HashData(wire).SequenceEqual(SHA256.HashData(payload)),
                "Transport compression changed the verified file bytes.");
        }
        foreach (var supported in new[] { false, true })
        {
            using var content = SharedSaveCompression.SeedContent(payload, supported);
            Require(content.Headers.ContentEncoding.Contains("gzip") == supported,
                "Seed compression did not preserve old-peer compatibility.");
            var context = new DefaultHttpContext();
            var wire = await content.ReadAsByteArrayAsync();
            context.Request.Body = new MemoryStream(wire);
            context.Request.ContentLength = wire.Length;
            if (supported) context.Request.Headers.ContentEncoding = "gzip";
            Require((await SharedSaveCompression.ReadPeerRequestAsync(context, payload.Length))!.SequenceEqual(payload),
                "A negotiated seed request did not round trip.");
        }
        var bomb = new DefaultHttpContext();
        bomb.Request.Headers.ContentEncoding = "gzip";
        bomb.Request.Body = new MemoryStream(compressed);
        bomb.Request.ContentLength = compressed.Length;
        Require(await SharedSaveCompression.ReadPeerRequestAsync(bomb, 1024) is null,
            "An expanded seed request bypassed the original decoded byte limit.");
        var unknown = new DefaultHttpContext();
        unknown.Request.Headers.ContentEncoding = "br";
        Require(await SharedSaveCompression.ReadPeerRequestAsync(unknown, 1024) is null,
            "Unsupported request compression was accepted.");
        unknown.Request.Headers.ContentEncoding = "gzip, br";
        Require(await SharedSaveCompression.ReadPeerRequestAsync(unknown, 1024) is null,
            "Layered request compression bypassed the fixed codec.");
        var oversized = new DefaultHttpContext();
        oversized.Request.Headers.ContentEncoding = "gzip";
        oversized.Request.ContentLength = 2049;
        oversized.Request.Body = new MemoryStream(compressed);
        Require(await SharedSaveCompression.ReadPeerRequestAsync(oversized, 2048) is null,
            "An oversized encoded request bypassed its declared length cap.");
        var malformed = new DefaultHttpContext();
        malformed.Request.Headers.ContentEncoding = "gzip";
        malformed.Request.Body = new MemoryStream([0, 1, 2, 3, 4]);
        var rejected = false;
        try { await SharedSaveCompression.ReadPeerRequestAsync(malformed, 1024); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Corrupt gzip was treated as original save bytes.");
        var canceled = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        canceled.Request.Headers.ContentEncoding = "gzip";
        canceled.Request.Body = new MemoryStream(compressed);
        rejected = false;
        try { await SharedSaveCompression.ReadPeerRequestAsync(canceled, payload.Length); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected, "Compressed requests ignored cancellation.");

        var data = new LocalData(Path.Combine(root, "shared-transfer-projection"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Guid.NewGuid();
        var group = new SharedWorldPeerRoot(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), profile, Guid.NewGuid());
        var other = group with { GroupId = Guid.NewGuid() };
        var version = SharedWorldService.SignVersion(new(1, group.GroupId, 1, null, profile,
            GameKinds.Valheim, "synthetic", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
            new(2456, false), [new("synthetic.db", payload.Length, Convert.ToHexString(SHA256.HashData(payload)))], "", "", ""), key);
        using var activity = SharedSaveTransferProgress.Begin(data, group, version.VersionHash);
        foreach (var phase in new[] { "Receiving", "Verifying", "Receipt" })
        {
            SharedSaveTransferProgress.Report(data, group, version, phase, 10, 20);
            Require(SharedSaveTransferProgress.Read(data, group) is { } status && SharedSaveTransferProgress.BlocksHosting(status),
                "A peer receive phase did not block local hosting.");
            Require(SharedSaveTransferProgress.Read(data, other) is null,
                "Transfer progress leaked into a different pinned world.");
        }
        SharedSaveTransferProgress.Clear(data, group, version.VersionHash);
        Require(SharedSaveTransferProgress.Read(data, group) is { } finishing && SharedSaveTransferProgress.BlocksHosting(finishing),
            "Completion removed an operation that still holds its transfer lease.");
        using var overlapping = SharedSaveTransferProgress.Begin(data, group, version.VersionHash);
        activity.Dispose();
        Require(SharedSaveTransferProgress.Read(data, group) is { } otherFlight && SharedSaveTransferProgress.BlocksHosting(otherFlight),
            "Completing one flight removed another flight's active verification.");
        overlapping.Dispose();
        Require(SharedSaveTransferProgress.Read(data, group) is null, "Completed transfer progress remained active.");
    }
}
