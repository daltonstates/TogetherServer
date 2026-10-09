using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic file facts and pure token/parser behavior only; no native picker, process, app or listener.
internal static class SetupImportPreviewChecks
{
    internal static void Run(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var checkRoot = Path.Combine(root, "setup-import-preview", Guid.NewGuid().ToString("N"));
        var sources = Path.Combine(checkRoot, "sources");
        Directory.CreateDirectory(sources);
        using var data = new LocalData(Path.Combine(checkRoot, "data"));
        var clock = new SyntheticClock();
        var selections = new SetupImportSelections(data, false, clock);
        var profileId = Guid.NewGuid();
        var zip = Path.Combine(sources, "Synthetic.ZIP");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("synthetic.txt").Open()))
            writer.Write("Only a synthetic source; no game integrity claim.");
        var modified = clock.GetUtcNow().AddHours(-2);
        File.SetLastWriteTimeUtc(zip, modified.UtcDateTime);
        var prepared = selections.Prepare(GameKinds.Factorio, profileId, zip);
        var preview = prepared.Preview;
        Require(prepared.Ok && preview is not null && preview.Kind == GameKinds.Factorio &&
            preview.ProfileId == profileId && preview.WorldId == "Synthetic" &&
            preview.SourceFiles.Count == 1 && preview.SourceFiles[0].Name == "Synthetic.ZIP" &&
            preview.TotalBytes == new FileInfo(zip).Length && preview.ModifiedUtc == modified &&
            preview.ExpiresUtc == clock.GetUtcNow() + TimeSpan.FromMinutes(10), "native source facts or expiry were wrong");
        Require(!Directory.Exists(data.FactorioServersRoot) && File.Exists(zip), "preview copied or altered a source");
        var json = JsonSerializer.Serialize(prepared, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(!json.Contains(sources, StringComparison.OrdinalIgnoreCase) && !json.Contains("selectedPath") &&
            json.Contains("\"sourceFiles\"") && json.Contains("\"totalBytes\""), "preview leaked its internal source path");
        Require(!selections.Resolve(preview!.SelectionId, GameKinds.Factorio, Guid.NewGuid()).Ok &&
            !selections.Resolve(preview.SelectionId, GameKinds.Terraria, profileId).Ok,
            "selection was usable across profile or game boundaries");
        var resolved = selections.Resolve(preview.SelectionId, GameKinds.Factorio, profileId);
        Require(resolved.Ok && resolved.SelectedPath == zip &&
            !JsonSerializer.Serialize(resolved, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Contains(zip),
            "valid selection did not resolve internally or its path serialized");
        Require(!selections.Resolve(preview.SelectionId, GameKinds.Factorio, profileId).Ok, "selection replay succeeded");

        var expired = selections.Prepare(GameKinds.Factorio, profileId, zip).Preview!;
        clock.Advance(TimeSpan.FromMinutes(10));
        Require(!selections.Resolve(expired.SelectionId, GameKinds.Factorio, profileId).Ok, "exact expiry boundary was accepted");
        var movedClock = selections.Prepare(GameKinds.Factorio, profileId, zip).Preview!;
        clock.MoveWallClock(TimeSpan.FromSeconds(-1));
        Require(!selections.Resolve(movedClock.SelectionId, GameKinds.Factorio, profileId).Ok, "clock rollback prolonged a selection");
        var monotonic = selections.Prepare(GameKinds.Factorio, profileId, zip).Preview!;
        clock.AdvanceMonotonic(TimeSpan.FromMinutes(10));
        Require(!selections.Resolve(monotonic.SelectionId, GameKinds.Factorio, profileId).Ok,
            "frozen wall time prolonged a selection beyond ten elapsed minutes");

        var changed = selections.Prepare(GameKinds.Factorio, profileId, zip).Preview!;
        File.SetLastWriteTimeUtc(zip, modified.AddMinutes(1).UtcDateTime);
        Require(selections.Resolve(changed.SelectionId, GameKinds.Factorio, profileId).Code == "ImportSourceChanged",
            "known source modification time was not rechecked");
        var changedSize = selections.Prepare(GameKinds.Factorio, profileId, zip).Preview!;
        using (var output = new FileStream(zip, FileMode.Append)) output.WriteByte(1);
        File.SetLastWriteTimeUtc(zip, modified.AddMinutes(1).UtcDateTime);
        Require(selections.Resolve(changedSize.SelectionId, GameKinds.Factorio, profileId).Code == "ImportSourceChanged",
            "known source length was not rechecked when the date stayed equal");

        var world = Path.Combine(sources, "Synthetic.wld");
        File.WriteAllBytes(world, [1, 2, 3, 4]);
        File.WriteAllBytes(world + ".bak", [5, 6]);
        File.SetLastWriteTimeUtc(world, modified.UtcDateTime);
        File.SetLastWriteTimeUtc(world + ".bak", modified.AddMinutes(1).UtcDateTime);
        var terraria = selections.Prepare(GameKinds.Terraria, profileId, world).Preview!;
        Require(terraria.SourceFiles.Count == 2 && terraria.TotalBytes == 6 &&
            terraria.ModifiedUtc == modified.AddMinutes(1) && terraria.SourceFiles[1].Name == "Synthetic.wld.bak",
            "Terraria preview did not match the ordinary importer's fixed companion inventory");
        File.Delete(world + ".bak");
        Require(selections.Resolve(terraria.SelectionId, GameKinds.Terraria, profileId).Code == "ImportSourceChanged",
            "a disappeared Terraria companion was silently omitted after preview");
        var withoutBackup = selections.Prepare(GameKinds.Terraria, profileId, world).Preview!;
        File.WriteAllBytes(world + ".bak", [7]);
        Require(selections.Resolve(withoutBackup.SelectionId, GameKinds.Terraria, profileId).Code == "ImportSourceChanged",
            "a newly added Terraria companion was copied without preview");

        var capped = new SetupImportSelections(data, false, clock);
        var tokens = Enumerable.Range(0, SetupImportSelections.MaximumEntries)
            .Select(_ => capped.Prepare(GameKinds.Factorio, profileId, zip)).ToArray();
        Require(tokens.All(result => result.Ok) &&
            capped.Prepare(GameKinds.Factorio, profileId, zip).Code == "TooManyImportSelections",
            "pending selection storage was not bounded at 32");
        Require(capped.Resolve(tokens[0].Preview!.SelectionId, GameKinds.Factorio, profileId).Ok,
            "capacity refusal evicted a still-valid selection");
        clock.Advance(TimeSpan.FromMinutes(10));
        Require(capped.Prepare(GameKinds.Factorio, profileId, zip).Ok, "expired entries did not release capacity");
        var staging = new SetupImportSelections(data, true, clock);
        Require(staging.Prepare(GameKinds.Factorio, profileId, zip).Code == "StagingFreshWorldRequired" &&
            staging.Prepare(GameKinds.Terraria, profileId, world).Code == "StagingFreshWorldRequired" &&
            !staging.Resolve(Guid.NewGuid(), GameKinds.Factorio, profileId).Ok, "staging allowed an existing source import");
        var managed = Path.Combine(data.RootPath, "synthetic-managed.zip");
        File.WriteAllBytes(managed, [1]);
        Require(!selections.Prepare(GameKinds.Factorio, profileId, managed).Ok &&
            !selections.Prepare(GameKinds.Factorio, profileId, world).Ok &&
            !selections.Prepare(GameKinds.Valheim, profileId, zip).Ok &&
            !selections.Prepare(GameKinds.Factorio, Guid.Empty, zip).Ok &&
            !selections.Prepare(GameKinds.Factorio, profileId, "relative.zip").Ok,
            "a managed, wrong-extension, unsupported or unbound source was accepted");
        Require(selections.Prepare(GameKinds.Factorio, profileId, null).Code == "Canceled", "native cancellation was not preserved");

        var missing = SetupImportSourceFacts.Read(Path.Combine(sources, "missing.wld"));
        Require(missing.Bytes is null && missing.ModifiedUtc is null &&
            SetupImportSourceFacts.TotalBytes([missing]) is null &&
            SetupImportSourceFacts.ModifiedUtc([missing]) is null &&
            SetupImportSourceFacts.TotalBytes([]) is null &&
            SetupImportSourceFacts.TotalBytes([new("a", long.MaxValue, modified), new("b", 1, modified)]) is null,
            "unavailable or overflowing source facts were invented as zero");
        Require(!SetupImportSourceFacts.SameKnownFacts(new("a", 1, modified), new("a", null, modified)),
            "a known size became unavailable without refusing confirmation");
        ValheimFacts(sources);
        Parsers(profileId);
        ReparseSources(selections, sources, zip, profileId);
    }

    private static void ValheimFacts(string sources)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var saveRoot = Path.Combine(sources, "valheim-synthetic");
        var folder = Path.Combine(saveRoot, "worlds_local");
        Directory.CreateDirectory(folder);
        var db = Path.Combine(folder, "Pair.db");
        var fwl = Path.Combine(folder, "Pair.fwl");
        File.WriteAllBytes(db, [1, 2, 3]);
        File.WriteAllBytes(fwl, [4, 5]);
        var first = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
        File.SetLastWriteTimeUtc(db, first.UtcDateTime);
        File.SetLastWriteTimeUtc(fwl, first.AddMinutes(1).UtcDateTime);
        var discovery = ValheimSetup.ScanRoots([], [saveRoot]);
        var pair = discovery.Worlds.Single();
        var browse = ValheimSetup.SelectWorldFile(db);
        Require(pair.SourceFiles is { Count: 2 } && pair.TotalBytes == 5 &&
            pair.ModifiedUtc == first.AddMinutes(1) && pair.SourceFiles[0].Name == "Pair.db" &&
            browse.SourceFiles is { Count: 2 } && browse.TotalBytes == pair.TotalBytes &&
            browse.ModifiedUtc == pair.ModifiedUtc, "discovery and native pair selection facts drifted");
        File.Delete(fwl);
        Require(ValheimSetup.PreviewWorldPair(saveRoot, "Pair") is null, "incomplete pair had complete source facts");
        File.WriteAllBytes(fwl, [4]);
        Directory.CreateDirectory(Path.Combine(folder, "Pair"));
        Require(ValheimSetup.PreviewWorldPair(saveRoot, "Pair") is null, "ambiguous pair/folder source had a precise copy preview");
    }

    private static void Parsers(Guid profileId)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var selection = Guid.NewGuid();
        var valid = $"{{\"selectionId\":\"{selection}\",\"profileId\":\"{profileId}\",\"kind\":\"Factorio\"}}";
        Require(SetupImportRequestParser.TryParse(Encoding.UTF8.GetBytes(valid), out var request) &&
            request == new SetupImportConfirmation(selection, profileId, GameKinds.Factorio), "fixed confirmation failed");
        foreach (var invalid in new[]
        {
            valid.Replace("Factorio", "factorio"), valid.Replace("Factorio", "Valheim"),
            valid.Replace(selection.ToString(), Guid.Empty.ToString()), valid.Replace(profileId.ToString(), profileId.ToString("N")),
            valid.Replace("\"kind\":\"Factorio\"", "\"kind\":null"),
            valid.Replace("\"kind\":\"Factorio\"", "\"kind\":{}"),
            valid.Replace("}", ",\"path\":\"ignored.zip\"}"), valid.Replace("}", ",\"command\":\"ignored\"}"),
            valid.Replace("}", $",\"profileId\":\"{profileId}\"}}"), valid.Replace("selectionId", "SelectionId"),
            "null", "[]", valid + "{}", new string(' ', SetupImportRequestParser.MaximumRequestBytes + 1) + valid
        }) Require(!SetupImportRequestParser.TryParse(Encoding.UTF8.GetBytes(invalid), out _), "arbitrary, duplicate or malformed confirmation was accepted");
        var prepare = $"{{\"profileId\":\"{profileId}\"}}";
        Require(SetupImportRequestParser.TryParsePrepare(Encoding.UTF8.GetBytes(prepare), out var parsed) && parsed == profileId &&
            !SetupImportRequestParser.TryParsePrepare(Encoding.UTF8.GetBytes(prepare.Replace("}", ",\"path\":\"ignored\"}")), out _) &&
            !SetupImportRequestParser.TryParsePrepare(Encoding.UTF8.GetBytes(prepare.Replace(profileId.ToString(), Guid.Empty.ToString())), out _),
            "browse preparation accepted anything except one nonempty profile ID");
    }

    private static void ReparseSources(SetupImportSelections selections, string sources, string zip, Guid profileId)
    {
        var link = Path.Combine(sources, "linked.zip");
        var linkedFolder = Path.Combine(sources, "linked-folder");
        var plainParent = Path.Combine(sources, "plain-parent");
        Directory.CreateDirectory(plainParent);
        File.Copy(zip, Path.Combine(plainParent, Path.GetFileName(zip)));
        try
        {
            File.CreateSymbolicLink(link, zip);
            Directory.CreateSymbolicLink(linkedFolder, plainParent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("SKIP Synthetic source-link cases: this test account cannot create symbolic links.");
            return;
        }
        if (selections.Prepare(GameKinds.Factorio, profileId, link).Ok ||
            selections.Prepare(GameKinds.Factorio, profileId, Path.Combine(linkedFolder, Path.GetFileName(zip))).Ok)
            throw new Exception("a source file or ancestor reparse point was accepted");
        var reviewed = Path.Combine(sources, "reviewed.zip");
        File.Copy(zip, reviewed);
        var preview = selections.Prepare(GameKinds.Factorio, profileId, reviewed).Preview!;
        File.Delete(reviewed);
        File.CreateSymbolicLink(reviewed, zip);
        if (selections.Resolve(preview.SelectionId, GameKinds.Factorio, profileId).Ok)
            throw new Exception("confirmation accepted a source replaced by a reparse point");
    }

    private sealed class SyntheticClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-08T15:00:00Z");
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan elapsed) { now += elapsed; timestamp += elapsed.Ticks; }
        internal void MoveWallClock(TimeSpan elapsed) => now += elapsed;
        internal void AdvanceMonotonic(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
