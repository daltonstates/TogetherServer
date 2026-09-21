# Public connection and pairing

The owner chose **public-IP access without requiring a VPN**. This is workable for a small group, but the companion API can request actions on the owner's PC. It must have stricter access controls than a game join port. Keep the public surface to a small HTTPS API; keep the Host GUI and local owner settings on loopback.

## Connections

- The Friend app initiates the connection to the Host. The Host does not try to open an inbound connection to a Friend PC behind a home router.
- The Host has a configurable TCP control port separate from Valheim's game port. No listener on a public interface is enabled by default. Explicitly display the bound address and port after the owner enables it.
- An Internet test must come from a Friend on another network. A localhost or same-LAN request is not proof that the public IP/port works. Detect and explain likely unroutable WAN/ISP NAT cases; do not silently alter the router or Windows Firewall.
- Host mode may use an outbound HTTPS request to ipify to display its current public IPv4 address. A successful lookup is only an address hint: it does not prove port forwarding, firewall access, game join, or Friend app reachability. No account or credential is sent; if the lookup fails or becomes stale, do not present it as a verified connection.
- The game network mode is a separate setting. Valheim's [official dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) states that the Steam backend normally needs the selected UDP port and the following port reachable (default 2456-2457), while its Crossplay backend uses a relay and does not require game-port forwarding. Neither mode supplies the companion-control connection.

## Host identity and Friend credentials

- Host mode generates a TLS identity. Its certificate/private key stays on the Host in Windows-protected storage. A current one-time invite carries the Host IPv4 address and port, full certificate fingerprint, device ID, and random pairing secret; the owner shares it through a trusted channel. Friend mode pins that identity and refuses mismatches without a "skip certificate checks" button.
- The Friend screen needs one pasted invite for current pairing. The Host's **Create invite and allow connections** action prepares the detected app address and enables the HTTPS listener in the same process. An older `TS1` invite still needs a separate Host IP and remains accepted until it expires. No Friend IP is entered. Each invite expires after 30 minutes and is replaced by a separately revocable saved device credential.
- Each Friend **device** gets a distinct, random, one-time pairing credential, expiration/rotation support, and owner-set Start/Stop permissions. Host stores only a verifier/hash; Friend stores its own credential using Windows-protected storage. Do not put credentials in URLs, logs, screenshots, or Git.
- Validate the credential, Host identity, request size, action kind, and permissions for every request. Bind lifecycle requests to an idempotency key so retries after a network timeout cannot start a second process.
- The owner can revoke one Friend device immediately. Its next request must fail clearly. An offline app learns about revocation when it reconnects.
- Rate-limit authentication and actions; record a small, local audit trail of pairing, revocation, enable/disable, and start/stop requests without recording secrets.

As of the project handoff, [Let's Encrypt supports public-IP certificates](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability), but they last about six days and need reliable automated renewal. Pinning the Host certificate in the manually shared password is the current direction for this app. The implementation must still verify the actual TLS and pairing behavior with two separate PCs; a local mock does not certify it.

## Remote disable and notices

`remoteControlsEnabled` is an owner-controlled switch separate from whether a game is running. When false, Host immediately denies **all remote Start/Stop requests** with a typed `RemoteControlsDisabled` result. Authenticated heartbeat/status may continue and includes `remoteControlsEnabled: false` and an optional owner notice for display. A Friend app online at the time shows it on its next poll; an offline Friend shows it on reconnect. If Host is unreachable, show Disconnected/Unknown, not a fabricated disable notice.

Per-Friend permission changes and revocation are checked by Host on each action even if the Friend UI has stale buttons. No Friend request may supply an executable, script, command line, working directory, world save path, or arbitrary settings update. Public input is a request for a fixed approved action only.

Remote Stop also requires an exact, unchanged `permittedlist.txt` in the selected Valheim save directory from server startup. The owner assigns a unique Valheim Platform User ID to each paired Friend PC and to the owner if the owner plays. TogetherServer creates a new list only in its own new-world folder or an imported copy while the server is offline; it never overwrites an existing list. Host requires fresh closed-game reports from every paired Friend and checks the owner's local game before signaling Stop. This policy does not prove a real player count or a public Friend join.

## Owner approval gates

This repository setup does not publish a listener, request a certificate, install a binary, or modify a firewall/router/DNS setting. During real deployment, stop and ask for the owner's explicit approval before changing public firewall/router/DNS configuration or requesting credentials. The owner must accept game terms and install any terms-gated dedicated-server binary personally. Do not send invites to friends from a tool without the owner's explicit authorization.
