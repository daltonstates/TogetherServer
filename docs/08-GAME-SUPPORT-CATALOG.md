# Game support catalog

Updated 2026-09-23. This is the exhaustive **evaluated backlog for TogetherServer**, not a claim that every multiplayer game ever released has been discovered. A game belongs here only when it is a plausible Windows-first local Host target or a frequently requested game whose current distribution model blocks local hosting. Recheck upstream documentation before implementation because server binaries, ports, protocols, and licensing change.

## What "support" means

| Level | Meaning |
| --- | --- |
| Built-in | A reviewed .NET driver is in the app. Fixture evidence and real-game acceptance are stated separately. |
| Next | Strong candidate for a built-in driver: a Windows dedicated/headless process plus a credible readiness, player-count, and graceful-stop path. |
| Candidate | Likely implementable, but its current Windows binary, query/admin protocol, save behavior, or terms still needs research. |
| Custom now | An owner may use the local Custom game script manager today. Counts are display-only until the exact configuration passes contract-v2 live certification. A match can authorize guarded Friend Stop, Restart, replacement, and idle shutdown, but remains owner-certified rather than official game support. |
| Blocked | No owner-run dedicated/headless server is currently known, distribution is restricted, or the game depends on publisher-hosted sessions. |

A built-in driver is accepted only after fixed arguments, declared ports, real readiness, authoritative zero/positive/unknown player counts, graceful Stop, process identity, a client join, and a recognizable save across restart have been checked. A Steam tool listing or community setup guide is candidate evidence only.

## Current built-in games

| Game | Driver status | Real-game gate |
| --- | --- | --- |
| Valheim | Built-in | Real join/save/restart and public Friend route remain separately recorded gates. |
| Minecraft: Java Edition | Built-in preview | Fixture-tested; owner-installed server join, player transition, and save/restart remain. |
| Minecraft: Bedrock Edition | Built-in preview | Fixture-tested; owner-installed server join, player transition, and save/restart remain. |

## Recommended built-in order

These give the best early coverage across common server shapes instead of adding many near-duplicate drivers at once.

| Wave | Games | Why this group |
| --- | --- | --- |
| 1 | Factorio; Terraria; Project Zomboid; Palworld; Satisfactory | Covers direct executable/headless launch, configuration files, console or admin APIs, UDP/TCP status, and different save layouts. |
| 2 | V Rising; 7 Days to Die; Enshrouded; Core Keeper; Don't Starve Together | Popular persistent co-op servers with Windows tooling, but authentication, query, or graceful-stop details need game-specific review. |
| 3 | ARK: Survival Ascended; ARK: Survival Evolved; Conan Exiles; Rust; Space Engineers | High demand but larger installs, more ports/settings, longer startup, and more complicated admin/query acceptance. |
| 4 | Source/GoldSrc family | One reviewed protocol/console foundation can cover several titles, but each game still needs explicit ports, app identity, and save/map behavior. |

Factorio documents a headless server and save-on-exit behavior; Terraria documents a dedicated server executable and config-file launch; Satisfactory has a Windows server plus query/HTTPS management interfaces; and V Rising publishes Windows dedicated-server and RCON instructions. Those upstream surfaces make them credible candidates, not accepted drivers.

## Windows-first candidate inventory

Every game below can use **Custom now** if the owner supplies reviewed scripts and the required server is already installed. "Next" is a recommendation for an official driver; "Candidate" means research first.

### Survival, crafting, and persistent co-op

| Game | Planned level | Main open gate |
| --- | --- | --- |
| 7 Days to Die | Next | Telnet/player query and save-confirmed shutdown. |
| Abiotic Factor | Candidate | Verify current Windows server tool, query, and graceful Stop. |
| ARK: Survival Ascended | Candidate | Long startup, multiple ports, RCON/count, save command, and large-install acceptance. |
| ARK: Survival Evolved | Candidate | A2S/RCON, save command, cluster paths, and shutdown acceptance. |
| Astroneer | Candidate | Server ownership/claim flow and authenticated status management. |
| Avorion | Candidate | Console/player list, save path, and shutdown behavior. |
| Barotrauma | Candidate | Config/content packages, console count, and save/session shutdown. |
| Citadel: Forged with Fire | Candidate | Current server availability and RCON/save behavior. |
| Conan Exiles | Candidate | A2S/RCON, multiple ports, mod/update behavior, and saved-world Stop. |
| Core Keeper | Next | Current server executable, Game ID lifecycle, player count, and exit/save evidence. |
| Craftopia | Candidate | Current dedicated-server channel, version compatibility, and status protocol. |
| DayZ | Candidate | Steam query, BattlEye/config identity, save persistence, and shutdown. |
| Don't Starve Together | Next | Cluster tokens, shard processes, player count, and coordinated shutdown. |
| Eco | Candidate | Web/RCON management authentication and save-confirmed Stop. |
| Empyrion: Galactic Survival | Candidate | Telnet/API status, scenario paths, and graceful save/stop. |
| Enshrouded | Next | Query/player count, save cadence, and graceful process exit. |
| Foundry | Candidate | Verify current server tool, query, and world persistence. |
| Frozen Flame | Candidate | Current dedicated-server support and authoritative player count. |
| HumanitZ | Candidate | Current server branch, query protocol, and save-confirmed Stop. |
| Icarus | Candidate | Session/profile identity, RCON/query path, and save behavior. |
| Last Oasis | Candidate | Ownership/login requirements and current self-host distribution. |
| Longvinter | Candidate | Query/player count and save/stop semantics. |
| Medieval Engineers | Candidate | Legacy support status, save path, and safe shutdown. |
| Miscreated | Candidate | Legacy server availability and query/admin behavior. |
| Myth of Empires | Candidate | Account/authorization requirements, RCON, and world persistence. |
| Necesse | Candidate | Headless launch, console player list, and save/stop validation. |
| No One Survived | Candidate | Current Windows server tool and status/admin surface. |
| Palworld | Next | RCON player list/shutdown, save completion, and versioned settings. |
| PixARK | Candidate | A2S/RCON, ports, save command, and current server distribution. |
| Project Zomboid | Next | Steam query/RCON, workshop updates, multi-port plans, and world Stop. |
| Reign of Kings | Candidate | Legacy availability and query/save behavior. |
| Rising World | Candidate | Current Java/native server shape, status, and save/stop. |
| Rust | Candidate | A2S/RCON, oxide/mod exclusions, save command, and restart acceptance. |
| Satisfactory | Next | Lightweight query/HTTPS auth, player state, explicit save, and graceful Stop. |
| Smalland: Survive the Wilds | Candidate | Current tool distribution, query, and save/stop evidence. |
| Soulmask | Candidate | RCON/query behavior, ports, and save-confirmed Stop. |
| Sons of the Forest | Candidate | Dedicated-server query, configuration, and save/stop behavior. |
| Space Engineers | Candidate | Windows service/process identity, VRage Remote API, mods, and save Stop. |
| Starbound | Candidate | Query/player list, universe ownership, and console shutdown. |
| Stationeers | Candidate | RCON/player state, ports, and save-confirmed Stop. |
| Stormworks: Build and Rescue | Candidate | Server executable lifecycle, query, and world persistence. |
| Subsistence | Candidate | Current dedicated-server support, query, and save/stop. |
| Sunkenland | Candidate | Verify current public server tool, player count, and graceful Stop. |
| Terraria | Next | Console player list, `save` plus `exit`, world selection, and real restart. |
| The Forest | Candidate | Steam query/player count, config paths, and save shutdown. |
| Unturned | Candidate | Steam query/RCON, workshop content, and save-confirmed Stop. |
| V Rising | Next | A2S/RCON or API count, authenticated shutdown, and persistence acceptance. |
| Vintage Story | Candidate | Server command/player list, mod set, and world save Stop. |
| Wurm Unlimited | Candidate | Java process identity, RMI/admin surface, database save, and Stop. |

### Shooters and Source-family servers

| Game | Planned level | Main open gate |
| --- | --- | --- |
| Alien Swarm: Reactive Drop | Candidate | Source launch preset, A2S, ports, and map/session shutdown. |
| Arma 3 | Candidate | Profile/mod arguments, Steam query, BattlEye, and mission shutdown. |
| Arma Reforger | Candidate | JSON config, backend registration, query, and save/session Stop. |
| Black Mesa | Candidate | Source driver variant, A2S, and map lifecycle. |
| Counter-Strike 1.6 | Candidate | GoldSrc launch preset, A2S, RCON, and legacy distribution. |
| Counter-Strike 2 | Candidate | Source 2 launch/config, A2S, RCON, and workshop/map lifecycle. |
| Counter-Strike: Source | Candidate | Source preset, A2S, RCON, and map lifecycle. |
| Day of Defeat | Candidate | GoldSrc preset, A2S, RCON, and legacy distribution. |
| Day of Defeat: Source | Candidate | Source preset, A2S, RCON, and map lifecycle. |
| Double Action: Boogaloo | Candidate | Source preset, current server app, and A2S. |
| Fistful of Frags | Candidate | Source preset, current server app, and A2S. |
| Garry's Mod | Candidate | Source preset, workshop collections, A2S/RCON, and map shutdown. |
| Half-Life | Candidate | GoldSrc preset, A2S, RCON, and legacy distribution. |
| Half-Life 2: Deathmatch | Candidate | Source preset, A2S, RCON, and map lifecycle. |
| Hell Let Loose | Candidate | Verify owner-accessible server distribution and admin/query interfaces. |
| Insurgency | Candidate | Source preset, A2S/RCON, and map lifecycle. |
| Insurgency: Sandstorm | Candidate | Steam query/RCON, tokens/config, ports, and shutdown. |
| Killing Floor | Candidate | Query/web admin, map lifecycle, and save/config handling. |
| Killing Floor 2 | Candidate | Query/web admin, workshop content, and shutdown. |
| Left 4 Dead 2 | Candidate | Source preset, A2S/RCON, lobby/map lifecycle, and idle behavior. |
| Mordhau | Candidate | RCON/player state, multiple ports, and map shutdown. |
| Natural Selection 2 | Candidate | Query/server web API, mods, and map lifecycle. |
| No More Room in Hell | Candidate | Source preset, A2S/RCON, and map lifecycle. |
| Quake Live | Candidate | Legacy dedicated executable, status protocol, and graceful quit. |
| Red Orchestra 2 / Rising Storm | Candidate | Query/web admin, ports, and map lifecycle. |
| Rising Storm 2: Vietnam | Candidate | Query/web admin, ports, and shutdown. |
| SCP: Secret Laboratory | Candidate | Current server distribution, query/API, and graceful shutdown. |
| Soldat | Candidate | Current server binary, text/admin protocol, and map lifecycle. |
| Squad | Candidate | Steam query/RCON, licensed server distinctions, ports, and shutdown. |
| Squad 44 | Candidate | Steam query/RCON, ports, and map shutdown. |
| Sven Co-op | Candidate | GoldSrc preset, A2S/RCON, and map lifecycle. |
| Team Fortress 2 | Candidate | Source preset, A2S/RCON, workshop/maps, and shutdown. |
| Tower Unite | Candidate | Current server tool, query/player count, and game-mode lifecycle. |
| Zombie Panic! Source | Candidate | Source preset, A2S/RCON, and map lifecycle. |

### Simulation, racing, sandbox, and strategy

| Game | Planned level | Main open gate |
| --- | --- | --- |
| American Truck Simulator | Candidate | Convoy server token/config, player status, and session Stop. |
| Assetto Corsa | Candidate | Server/config pair, UDP/TCP ports, entry list, and session Stop. |
| Assetto Corsa Competizione | Candidate | Server config/results, status, and graceful session shutdown. |
| Automobilista 2 | Candidate | Dedicated tool distribution, config, status, and session Stop. |
| BeamMP | Candidate | Community server licensing/versioning, HTTP status, and shutdown. |
| Colony Survival | Candidate | Headless server commands, player list, and world save Stop. |
| DCS World Dedicated Server | Candidate | Large updater/install, WebGUI auth, mission/player status, and shutdown. |
| Euro Truck Simulator 2 | Candidate | Convoy server token/config, player status, and session Stop. |
| Factorio | Next | Fixed save/config launch, RCON/player state, save command, and exit. |
| Farming Simulator 22 | Candidate | Game license/web admin requirements, savegame slot, and shutdown. |
| Farming Simulator 25 | Candidate | Game license/web admin requirements, savegame slot, and shutdown. |
| IL-2 Sturmovik: Great Battles | Candidate | DServer account/config, player status, mission rotation, and Stop. |
| Luanti (Minetest) | Candidate | Config/world selection, status protocol, and shutdown. |
| Mindustry | Candidate | Java headless launch, server commands/player list, save, and Stop. |
| Neverwinter Nights: Enhanced Edition | Candidate | Server arguments, status, module saves, and shutdown. |
| OpenRA | Candidate | Headless server packaging, lobby status, and match lifecycle. |
| OpenTTD | Candidate | Dedicated launch, admin network/player status, save, and shutdown. |
| RaceRoom Dedicated Server | Candidate | Web UI/API status and session shutdown. |
| rFactor | Candidate | Legacy distribution, config, status, and session Stop. |
| rFactor 2 | Candidate | SteamCMD tool, session config, status, and shutdown. |
| Teeworlds / DDNet | Candidate | Server binary, status protocol, maps, and shutdown. |
| Veloren | Candidate | Current Windows server binary, query/player list, persistence, and Stop. |
| Wreckfest | Candidate | Dedicated tool/config, Steam query, and event shutdown. |
| Xonotic | Candidate | Dedicated launch, status/RCON-like console, maps, and Stop. |

### Community-server platforms

These can technically fit the Custom manager but need separate licensing and trust review before any built-in support.

| Platform | Planned level | Main open gate |
| --- | --- | --- |
| FiveM / FXServer | Candidate | Cfx.re account/license key, artifacts, resources, status API, and graceful Stop. |
| RedM / FXServer | Candidate | Cfx.re account/license key, artifacts, resources, status API, and graceful Stop. |
| Multi Theft Auto | Candidate | Third-party server distribution, resources, query, and shutdown. |
| open.mp / SA-MP | Candidate | Third-party server distribution, scripts, query, and shutdown. |
| TES3MP | Candidate | Community engine/version compatibility, plugins, player query, and save Stop. |

## Popular games currently blocked

This review did not verify a supported owner-run dedicated/headless path for these frequently requested games. That is a planning blocker, not proof that no new or community server exists. Recheck the publisher before answering a future request, and do not advertise support unless upstream adds a suitable path or the project explicitly accepts a community implementation.

| Game | Current blocker |
| --- | --- |
| Among Us | Publisher-hosted sessions; no supported owner-run dedicated server. |
| Baldur's Gate 3 | Player-hosted sessions; no supported headless dedicated server. |
| Borderlands series | Player-hosted sessions; no supported headless dedicated server. |
| Deep Rock Galactic | Peer/player hosting; no supported dedicated server. |
| Grounded | Shared worlds/player hosting; no supported dedicated server. |
| Green Hell | Player-hosted co-op; no supported dedicated server. |
| Helldivers 2 | Publisher-hosted service; no owner-run dedicated server. |
| Lethal Company | Player-hosted lobbies; no supported dedicated server. |
| Monster Hunter series | Player-hosted/publisher matchmaking; no supported dedicated server. |
| No Man's Sky | Publisher matchmaking/player sessions; no owner-run dedicated server. |
| Phasmophobia | Player-hosted/publisher relay sessions; no supported dedicated server. |
| Raft | Player-hosted co-op; no supported dedicated server. |
| Remnant series | Player-hosted sessions; no supported dedicated server. |
| RimWorld | Multiplayer requires third-party mods; no official dedicated server. |
| Sea of Thieves | Publisher-hosted service; no owner-run dedicated server. |
| Stardew Valley | Player-hosted co-op; no supported headless dedicated server. |
| Subnautica | No official multiplayer or dedicated server. |

## Research sources and refresh rule

Use primary sources for a driver decision. Starting points used for this catalog include:

- Steamworks [Game Servers](https://partner.steamgames.com/doc/features/multiplayer/game_servers?language=english) and the game publisher's current server documentation.
- Factorio's [Multiplayer](https://wiki.factorio.com/Multiplayer) documentation.
- The Official Terraria Wiki's [dedicated server guide](https://terraria.wiki.gg/wiki/Guide:Setting_up_a_Terraria_server).
- The Official Satisfactory Wiki's [Dedicated servers](https://satisfactory.wiki.gg/wiki/Dedicated_servers) documentation and shipped HTTPS API description.
- Stunlock Studios' [V Rising dedicated-server instructions](https://github.com/StunlockStudios/vrising-dedicated-server-instructions).
- Valheim's [dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) and Microsoft's Minecraft server documentation already referenced elsewhere in this repository.

Before promoting any row to Built-in, verify the current owner-download path and terms, exact Windows executable, fixed launch settings, every declared port, local readiness, zero/one/disconnect counts, graceful Stop, save/restart behavior, and the intended Friend network route. Never infer those facts from this backlog or from a generic Steam server listing.
