using System.Text;
using Microsoft.AspNetCore.Http;
using TogetherServer;

internal static class LocalSharedLiveOrphanRequestChecks
{
    internal static async Task RunAsync()
    {
        var hash = new string('A', 64);
        var good = Post($"{{\"versionHash\":\"{hash}\"}}", header: true);
        var accepted = await LocalSharedLiveOrphanRequest.ReadPostAsync(good, friendMode: false);
        Require(accepted.VersionHash == hash && accepted.Rejection is null,
            "the fixed Host request rejected the exact status hash");

        var noHeader = await LocalSharedLiveOrphanRequest.ReadPostAsync(
            Post($"{{\"versionHash\":\"{hash}\"}}", header: false), friendMode: false);
        Require(noHeader.VersionHash is null && Status(noHeader.Rejection) == 403,
            "a POST without the local header reached live recovery");
        var friend = await LocalSharedLiveOrphanRequest.ReadPostAsync(
            Post($"{{\"versionHash\":\"{hash}\"}}", header: true), friendMode: true);
        Require(friend.VersionHash is null && Status(friend.Rejection) == 409,
            "Friend mode reached live recovery");
        foreach (var body in new[]
        {
            $"{{\"versionHash\":\"{hash}\",\"path\":\"C:/world\"}}",
            $"{{\"versionHash\":\"{hash}\",\"command\":\"stop\"}}",
            $"{{\"versionHash\":\"{hash}\",\"versionHash\":\"{hash}\"}}",
            $"{{\"versionHash\":\"{new string('A', 63)}\"}}",
            $"{{\"versionHash\":\"{new string('g', 64)}\"}}",
            $"{{\"versionHash\":\"{new string('a', 64)}\"}}",
            "[]", "{}", "not-json"
        })
        {
            var result = await LocalSharedLiveOrphanRequest.ReadPostAsync(
                Post(body, header: true), friendMode: false);
            Require(result.VersionHash is null && Status(result.Rejection) == 400,
                "the fixed Host request accepted an extra field or malformed hash");
        }
        var query = Post($"{{\"versionHash\":\"{hash}\"}}", header: true);
        query.Request.QueryString = new QueryString("?path=C:/world");
        Require(Status((await LocalSharedLiveOrphanRequest.ReadPostAsync(query, false)).Rejection) == 400,
            "the fixed Host request accepted a query");
        var oversized = await LocalSharedLiveOrphanRequest.ReadPostAsync(
            Post(new string('x', 129), header: true), false);
        Require(Status(oversized.Rejection) == 400, "the fixed Host request accepted an oversized body");

        var get = new DefaultHttpContext();
        get.Request.Headers["X-TogetherServer-Local"] = "1";
        Require(LocalSharedLiveOrphanRequest.RejectGet(get, false) is null,
            "the fixed Host status rejected a trusted GET");
        Require(Status(LocalSharedLiveOrphanRequest.RejectGet(get, true)) == 409,
            "Friend mode reached the Host status");
        get.Request.Headers.Remove("X-TogetherServer-Local");
        Require(Status(LocalSharedLiveOrphanRequest.RejectGet(get, false)) == 403,
            "an untrusted GET reached the Host status");
        get.Request.Headers["X-TogetherServer-Local"] = "1";
        get.Request.QueryString = new QueryString("?path=C:/world");
        Require(Status(LocalSharedLiveOrphanRequest.RejectGet(get, false)) == 400,
            "the Host status accepted a query");
        Console.WriteLine("PASS shared live owner API shape: exact hash, Host/header gate, no query or extra input");
    }

    private static DefaultHttpContext Post(string body, bool header)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (header) context.Request.Headers["X-TogetherServer-Local"] = "1";
        return context;
    }

    private static int? Status(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
