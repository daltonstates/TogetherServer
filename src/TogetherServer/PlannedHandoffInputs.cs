using System.Text.Json;

namespace TogetherServer;

internal static class PlannedHandoffInputs
{
    private static JsonDocument Object(ReadOnlySpan<byte> bytes, params string[] names)
    {
        if (bytes.Length is 0 or > 1024) throw new InvalidDataException("Use the fixed hosting request.");
        var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 2 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length ||
            root.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
            root.EnumerateObject().Any(item => !names.Contains(item.Name, StringComparer.Ordinal)))
        { document.Dispose(); throw new InvalidDataException("Use only the fixed hosting choices."); }
        return document;
    }

    internal static Guid Selection(ReadOnlySpan<byte> bytes)
    {
        using var document = Object(bytes, "successorDeviceId");
        return Id(document.RootElement, "successorDeviceId");
    }

    internal static Guid Continuation(ReadOnlySpan<byte> bytes)
    {
        using var document = Object(bytes, "requestId");
        return Id(document.RootElement, "requestId");
    }

    internal static PlannedHandoffResponse Response(ReadOnlySpan<byte> bytes)
    {
        using var document = Object(bytes, "requestId", "accept");
        if (document.RootElement.GetProperty("accept").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Accept or decline hosting explicitly.");
        return new(Id(document.RootElement, "requestId"), document.RootElement.GetProperty("accept").GetBoolean());
    }

    private static Guid Id(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String &&
        root.GetProperty(name).TryGetGuid(out var value) && value != Guid.Empty ? value :
        throw new InvalidDataException("Choose the exact current hosting request.");

    internal static PlannedHandoffPreparation Preparation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 16384) throw new InvalidDataException("Use the exact prepared save and signed contact.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 6 });
        static void Fields(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
                value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != names.Length ||
                value.EnumerateObject().Any(item => !names.Contains(item.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("The signed preparation contains unexpected fields.");
        }
        var root = document.RootElement;
        Fields(root, "requestId", "versionHash", "contact", "setupIdentity");
        _ = Id(root, "requestId");
        var contact = root.GetProperty("contact");
        Fields(contact, "schema", "root", "deviceId", "publicKey", "revision", "routes", "signature");
        Fields(contact.GetProperty("root"), "ownerPublicKey", "profileId", "groupId");
        var routes = contact.GetProperty("routes");
        if (routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() is < 1 or > 4)
            throw new InvalidDataException("Choose the selected PC's signed connection.");
        foreach (var route in routes.EnumerateArray()) Fields(route, "address", "certificateSha256");
        var value = JsonSerializer.Deserialize<PlannedHandoffPreparation>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 6 });
        if (value is null || value.VersionHash is not null && !SharedWorldPeerTrust.ValidHash(value.VersionHash) ||
            !SharedWorldPeerTrust.ValidHash(value.SetupIdentity) || value.Contact is null ||
            !WorldPeerService.VerifyContact(value.Contact))
            throw new InvalidDataException("The prepared save or signed contact failed verification.");
        return value;
    }
}
