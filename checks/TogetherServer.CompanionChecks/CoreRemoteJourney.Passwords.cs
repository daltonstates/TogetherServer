using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;

internal static partial class CoreRemoteJourney
{
    // Uses the existing packaged Host and both Friend proxies. Their ordinary
    // pinned HTTPS transports and protected password store remain authoritative;
    // no injected handler, real game binary or extra app process is involved.
    private static async Task RunPinnedPasswordJourneyAsync(HttpClient owner, HttpClient first,
        HttpClient second, Guid profileId, Guid firstDevice, string initialPassword, Action<string> passed)
    {
        var firstView = await WaitForConnectedAsync(first);
        var secondView = await WaitForConnectedAsync(second);
        Require(firstView.ConnectionId != Guid.Empty && secondView.ConnectionId != Guid.Empty &&
            firstView.HostId == secondView.HostId && firstView.Endpoint == secondView.Endpoint &&
            firstView.HostCapabilities.Contains(CompanionProtocol.GamePasswordCapability) &&
            secondView.HostCapabilities.Contains(CompanionProtocol.GamePasswordCapability),
            "password journey requires two authenticated saved connections to the same capable Host");
        var devices = await Devices();
        var firstBefore = devices.Single(device => device.Id == firstDevice);
        var secondDevice = devices.Single(device => device.Id != firstDevice).Id;
        Require(devices.Count == 2 && devices.All(device => device.Paired && !device.Revoked &&
            !device.CanViewPassword && device.ServerPermissions?.All(permission => !permission.CanViewPassword) != false),
            "pairing or lifecycle permissions granted password access by default");
        var runBefore = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot", Json))!.Runs.Single(run => run.ProfileId == profileId);
        Require(runBefore.State == "Offline" && runBefore.ProcessId is null, "password checks require the stopped synthetic server");

        async Task<IReadOnlyList<DeviceView>> Devices()
        {
            var view = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
            return view.GetProperty("devices").Deserialize<DeviceView[]>(Json) ?? throw new Exception("password device view was empty");
        }
        Task<GamePasswordResult> Reveal(HttpClient client, Guid connectionId) =>
            ReadPasswordFromFriendAsync(client, profileId, connectionId);
        async Task Denied(HttpClient client, Guid connectionId, string code)
        {
            var result = await Reveal(client, connectionId);
            Require(!result.Ok && result.Code == code && result.Password is null && result.GamePasswordChangedUtc is null,
                "password denial returned a secret, private change hint or incorrect typed refusal");
        }
        Task SecondDenied() => Denied(second, secondView.ConnectionId, "PermissionDenied");
        async Task Grant(bool enabled)
        {
            Require((await PutAsync<DevicePermissionRequest, PairingDecision>(owner,
                $"/api/local/devices/{firstDevice}/permissions",
                new(false, false, Scope: "password", CanViewPassword: enabled))).Ok,
                "the separate password grant could not be changed");
            var current = await Devices();
            var granted = current.Single(device => device.Id == firstDevice);
            Require(granted.CanViewPassword == enabled && granted.CanStart == firstBefore.CanStart &&
                granted.CanStop == firstBefore.CanStop && granted.AssignedProfileIds.SequenceEqual(firstBefore.AssignedProfileIds) &&
                !current.Single(device => device.Id == secondDevice).CanViewPassword,
                "changing password access changed lifecycle rights, assignments or another Friend's access");
        }
        async Task<GamePasswordResult> Current(string expected)
        {
            var result = await Reveal(first, firstView.ConnectionId);
            Require(result.Ok && result.Code == "GamePasswordRevealed" && result.ProfileId == profileId &&
                result.Password == expected && result.GamePasswordChangedUtc is not null,
                "the authorized pinned password read did not return the current protected value");
            var firstStatus = await first.GetFromJsonAsync<FriendView>("/api/local/snapshot", Json);
            var secondStatus = await second.GetFromJsonAsync<FriendView>("/api/local/snapshot", Json);
            Require(!JsonSerializer.Serialize(firstStatus, Json).Contains(expected, StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(secondStatus, Json).Contains(expected, StringComparison.Ordinal),
                "a password escaped its explicit reveal response into Friend status");
            await SecondDenied();
            return result;
        }

        await Denied(first, firstView.ConnectionId, "PermissionDenied");
        await SecondDenied();
        passed("pinned password defaults deny both isolated Friends despite lifecycle access");

        await Grant(true);
        var initial = await Current(initialPassword);
        var replacement = "core-current-" + Guid.NewGuid().ToString("N");
        Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
            $"/api/local/profiles/{profileId}/password", new(replacement))).Ok,
            "the Host could not change its stopped synthetic server password");
        var changed = await Current(replacement);
        Require(changed.GamePasswordChangedUtc != initial.GamePasswordChangedUtc,
            "changing the protected password did not update its authorized change hint");
        passed("only the granted Friend reveals the current password over pinned HTTPS, including a Host change");

        await Grant(false);
        await Denied(first, firstView.ConnectionId, "PermissionDenied");
        await SecondDenied();
        passed("password grant revocation denies the next read without changing lifecycle rights or the other Friend");

        await Grant(true);
        Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
            $"/api/local/devices/{firstDevice}/servers", new([]))).Ok, "the Host could not withdraw the first Friend's assignment");
        await Denied(first, firstView.ConnectionId, "PermissionDenied");
        await SecondDenied();
        Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
            $"/api/local/devices/{firstDevice}/servers", new([profileId]))).Ok, "the Host could not restore the exact assignment");
        _ = await Current(replacement);
        passed("unassignment immediately denies password access and restoring the exact assignment restores its current value");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var expires = await PutAsync<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
            $"/api/local/devices/{firstDevice}/access-expiry", new(AccessExpiresUtc: deadline));
        Require(expires.Ok && expires.AccessExpiresUtc == deadline, "the Host could not set the synthetic access deadline");
        var untilExpired = deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (untilExpired > TimeSpan.Zero) await Task.Delay(untilExpired);
        await Denied(first, firstView.ConnectionId, "AccessExpired");
        await SecondDenied();
        var expiredDevice = (await Devices()).Single(device => device.Id == firstDevice);
        Require(expiredDevice.AccessExpired && !expiredDevice.Revoked && expiredDevice.Paired &&
            expiredDevice.CanViewPassword && expiredDevice.CredentialExpiresUtc == firstBefore.CredentialExpiresUtc,
            "owner expiry revoked or rotated the credential instead of independently denying access");
        Require((await PutAsync<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
            $"/api/local/devices/{firstDevice}/access-expiry", new(Clear: true))).Ok,
            "the Host could not clear the synthetic owner deadline");
        _ = await Current(replacement);
        passed("owner access expiry denies the password over pinned HTTPS and clearing it restores the unchanged credential");

        Require((await PostAsync<object, PairingDecision>(owner,
            $"/api/local/devices/{firstDevice}/revoke", new { })).Ok, "the Host could not revoke the synthetic first Friend");
        await Denied(first, firstView.ConnectionId, "Revoked");
        await Denied(first, firstView.ConnectionId, "Revoked");
        await SecondDenied();
        var revoked = await PostAsync<object, FriendView>(first, "/api/local/friend/poll", new { });
        var remaining = await WaitForConnectedAsync(second);
        var runAfter = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot", Json))!.Runs.Single(run => run.ProfileId == profileId);
        Require(revoked.State == "Revoked" && remaining.ConnectionId == secondView.ConnectionId &&
            remaining.Profiles.Single().Id == profileId && !remaining.Profiles.Single().CanViewPassword &&
            runAfter.State == "Offline" && runAfter.ProcessId is null && runAfter.RunOperationId == runBefore.RunOperationId,
            "device revocation lost isolation or password reads caused lifecycle work");
        passed("pinned device revocation denies repeated password reads while the other Friend stays authenticated and denied");
    }

    private static async Task<GamePasswordResult> ReadPasswordFromFriendAsync(HttpClient client, Guid profileId, Guid connectionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local/friend/{profileId}/game-password/reveal")
        { Content = JsonContent.Create(new { connectionId }, options: Json) };
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
            "the fixed local password proxy failed or omitted no-store");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var result = document.RootElement.Deserialize<GamePasswordResult>(Json);
            Require(result is not null && (result.Ok || !document.RootElement.TryGetProperty("password", out _)),
                "a denied password response included the secret field");
            return result!;
        }
        catch (JsonException) { throw new Exception("the password proxy returned an invalid typed response"); }
    }
}
