using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class FeatureInputChecks
{
    internal static void Run()
    {
        static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        Require(FeatureRequestParser.TryVersion(Bytes("{\"version\":\"1.2.3\"}"), out var version) && version == "1.2.3", "fixed version input failed");
        foreach (var payload in new[] { "{\"version\":\"1.2\",\"path\":\"C:\\\\private\"}", "{\"version\":\"1.2\",\"version\":\"2.0\"}", "{\"Version\":\"1.2\"}" })
            Require(!FeatureRequestParser.TryVersion(Bytes(payload), out _), "arbitrary/duplicate input accepted");
        Require(!FeatureRequestParser.TryBookmark(Bytes("{\"label\":\"A\",\"pinned\":true,\"command\":\"unsafe\"}"), out _), "backup input accepted a command");
        Require(FeatureRequestParser.TryNotice(Bytes("{\"text\":null,\"expectedRevision\":0}"), out _) &&
            !FeatureRequestParser.TryNotice(Bytes("{\"text\":\"rules\",\"expectedRevision\":-1}"), out _) &&
            !FeatureRequestParser.TryNotice(Bytes("{\"text\":\"rules\",\"expectedRevision\":0,\"expectedRevision\":1}"), out _), "notice revision input is ambiguous");
        foreach (var wrongType in new[] { "\"0\"", "null", "{}", "true", "[]", "9007199254740992" })
            Require(!FeatureRequestParser.TryNotice(Bytes($"{{\"text\":\"rules\",\"expectedRevision\":{wrongType}}}"), out _), "wrong-type or inexact notice revision accepted");
    }
}
