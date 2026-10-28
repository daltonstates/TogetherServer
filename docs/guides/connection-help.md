# Connection help

[All documentation](../README.md) · [Hosting](hosting.md) · [Joining](joining.md)

TogetherServer's Friend connection and the game's join connection are separate.
A working app connection does not prove that someone can join the game.

## Who needs an inbound route?

The PC hosting the server needs a reachable HTTPS Friend-control port. A Friend
using Join makes an outbound connection and does not forward a port for it.
A PC hosting its own server has a separate Host route to consider.

| Route | What you arrange |
| --- | --- |
| Direct Internet | A routable public IPv4 address, the Host firewall allowance, and router forwarding for the configured Friend TCP port. Shared-address NAT or ISP filtering can prevent this. |
| Private mesh | An owner-installed and configured private network. TogetherServer can read Tailscale or ZeroTier adapter addresses. |
| Advanced address | An owner-managed IPv4 route, such as a WireGuard network. |

TogetherServer does not install mesh clients, change their policy, configure
routers, alter firewall/DNS settings, or provide a cloud relay. Every route
still uses pinned HTTPS identity, separate PC credentials, server assignments,
and permissions. The owner's management interface remains on loopback.

For direct Internet access, the normal Friend-control default is TCP 5131.
Use the configured port shown in the app. Forward it to the Host PC's shown
LAN address. The isolated development app defaults to TCP 5132.

The game needs its own route. Use the game ports on the server card. Valheim
Crossplay's game relay does not provide the TogetherServer control connection.
Factorio's RCON port must stay unforwarded.

Shared Host updates label peer addresses **TogetherServer HTTPS**. Connect to
the Host, then open **Join** for its current game address and port. Those two
ports can differ; a signed hosting report alone does not confirm game readiness.

## Check the connection in order

Open **This PC settings > Network** for Host connection controls and help.
Use **Connection Doctor** for the ordered checks; a saved Friend connection
also has its own Connection Doctor.

| Evidence | What it establishes |
| --- | --- |
| Public address lookup | An address hint. It does not test inbound access. |
| **Open on PC** | Windows sees a local listener. |
| **Reachable outside network** | The optional outside TCP check reached the configured Friend port. It has not authenticated to the app. |
| **Friend connected** | A recent pinned, authenticated Friend heartbeat. This alone does not establish that the PC is on a different network. |
| Game Ready / local endpoint reply | The driver's local readiness evidence, where supported. |
| **Game endpoint answered from this PC** | A supported fixed game query answered from that Friend PC. |
| A real game join and saved restart | People joined the intended server and verified a recognizable saved change after graceful Stop/restart. |

**Test Friend app port from internet** uses portchecker.io for a read-only
TCP check. It receives the public address/port needed for that check, not a
server code or credential. Checker failures are inconclusive. A failed result
does not identify which network layer blocked the connection.

## If Connect fails

| Result | Check next |
| --- | --- |
| Timeout or closed port | Keep the Host app running; check its listener, code address, Host firewall, router target/port, and ISP/shared-address NAT. |
| Identity mismatch | Verify the code or recover the existing pinned identity with the owner. Never bypass the certificate check. |
| Approval pending | Have the Host approve this PC locally. |
| Access expired | Have the Host extend or clear the owner deadline. Keep the saved connection. |
| Revoked access | Ask the owner whether this PC should receive fresh access. |
| No assigned server | Have the owner review this PC's server assignments. |
| App connects but game does not | Check game state, edition/version, address and game port, password, and the separate game route. |

A changed Host address can be recovered through the Friend server's
**Settings > Change Host address** if the existing pinned certificate and
device access both verify at the new endpoint. A replacement certificate is not trusted merely because
it appears at that address.

## Test with another person

After Start, **Test with a Friend** can observe a fresh authenticated heartbeat
and a server-reported player-count change. The people still confirm the
intended game join and a recognizable change surviving graceful restart.
The Acceptance Recorder keeps explicit owner reports for the exact setup;
changing the game, world, executable, ports, or route makes them stale.

Use a Friend PC on another network to test an Internet route. Loopback and
same-LAN tests are useful but cannot establish public reachability. A stopped
game cannot have its game route confirmed by an app-port test. Shared-hosting
preparation has additional route checks in the [sharing guide](backups-and-sharing.md).

## Share a support report

Use owner diagnostics and **Copy report** or **Download JSON** for a bounded
`TogetherServer-support-report.json`. It includes typed setup/status summaries,
local port evidence, recovery/update state, and log metadata. It excludes
credentials, codes, passwords, scripts, private paths, addresses, raw logs,
player/chat identity, and worlds.

Refreshing diagnostics does not start or stop a game or change network policy.
Review any extra screenshots or text yourself before sharing them; a raw
screen may contain details that the report deliberately leaves out.
