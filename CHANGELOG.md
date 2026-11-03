# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). While the version is 0.x, a new minor version may change the network protocol, so **all players must run the same version**.

## [Unreleased]

## [0.1.2] - 2026-11-04

### Fixed

- Clients could not carry dead bodies. A dead body on a client stayed kinematic, so the game's force-based hold could not move it. A held body now has real physics on the holder's side and is handed back to the host after the throw or drop.
- Enemies crab-walked on clients. The game turns an enemy's `visual` child towards its walking direction and leaves the root at its spawn rotation; the mod streamed the root. The host now streams the facing of live enemies and clients apply it to `visual`.
- Client punches often missed. Enemy colliders on clients kept their shrunken in-truck shape because the game restores it in code the mod disables on clients. The walking capsule is now restored when an enemy leaves the truck.
- Cans a client vacuumed and threw could fly past the reward pipe unpaid, because the host skipped the game's own grab handling for vacuum grabs.
- Skill tree nodes bought by another player did not show their purchased colours on clients.

### Added

- Log lines (rate-limited) when a client grab is refused, a client hit is dropped, or a can sits in the reward pipe for 3 seconds without being paid.
- Debug drivers for grabbing bodies, punching, vacuuming, enemy facing and skill colours (see the header of `Debug/DebugModule.cs`).

All players must update: 0.1.2 refuses older versions.

## [0.1.1] - 2026-11-03

### Fixed

- Mouse cursor stayed visible in game. Closing the F7 panel restored the cursor state saved when the panel opened, so a panel opened at the main menu and closed in game left the cursor free. A client that joined a running day could also miss the game's own cursor lock. While online, the cursor is now kept locked during the day and night (not while paused, in the shop, down, or with the F7 panel open), and closing the panel in game locks it.

### Added

- `--coop-debug-closemenu=seconds` debug switch (pairs with `--coop-debug-openmenu`).

All players must update: 0.1.0 and 0.1.1 refuse each other.

## [0.1.0] - 2026-11-03

First public release.

### Added

- Online co-op for 2 to 4 players, host-authoritative: the host runs the truck, enemies, grinders, payouts, day loop and the save file.
- Steam transport (Steam Networking Messages, relayed by Steam) with a friends list in the F7 menu, host SteamID64 with a Copy button, friends who are hosting listed first, join by SteamID64, and the `--coop-join-steam=<SteamID64>` launch switch.
- Direct TCP transport (default port 27555) for LAN, VPN or port-forwarded play, with `--coop-host-direct` and `--coop-join-direct=ip:port`.
- Shared wallet, skill tree, permanent upgrades, upgrade points, rebirth and day clock. Purchases are requested by clients and paid once by the host. New players receive a full snapshot when they join.
- Player-count balancing (`PaceMode = Neutral`): `w = N^0.8`, more enemies per truck and a bigger field cap, grinders `k` times faster, every payout divided by the realized `k = E_eff / E_base` with an exact fractional carry, `N` latched per day. `Free` mode, `WorkloadExponent`, `PerfCap`, `ScaleConveyor` and `ScaleFirstBloodCounters` as valves. See `docs/BALANCING.md`.
- Per-day balance telemetry: one `[DAYSTATS]` line per day in the log (players, enemy supply, `k`, kills, can units, money in and out).
- Mod-version handshake: a joiner with a different mod version is refused with a message on both sides.
- On-screen message box (about 7 seconds) for refusals, failed joins and lost connections, so the F7 panel does not have to be open.
- Larger name tag with an opaque background that scales with screen height and distance.
- Enemy replication: the host simulates, clients show puppets and send hits that the host applies through the game's own damage code. Kills, blood, turrets, robots and the truck are replicated.
- Can, crate and body replication with a grab, drop, throw and vacuum ownership protocol.
- Remote player avatars: tinted capsule with hard hat, name tag with weapon label, muzzle flash and shot sounds, melee swing, flamethrower and vacuum cues, down pose, interpolated 20 Hz pose stream.
- Team flow: day timer slaved to the host, ready-up quorum for the shop and elevators (`All`, `Majority`, `AllWithTimeout`), ending barrier, respawn after `RespawnSeconds` instead of a day-ending death, team wipe when everyone is down, pause that does not freeze other players, joining a running day.
- Slot machine rolled by the host, optional `MaxBetFraction` cap.
- F7 menu (key configurable) and a one-line HUD showing role, players, `N`, `k` and ping.
- BepInEx config file `com.eren.canemallcoop.cfg` with 20 settings (including `PeerTimeoutSeconds`, `SteamConnectTimeoutSeconds`, `SteamFriendsOnly`, `MaxSpawnBatch` and `IncomeDivisorExponent`).
- `scripts/package.py`, which builds the `full` and `plugin-only` release zips.
- Unit tests for all pure logic (protocol, codecs, interpolation, pace math, quorum rules, state machines).

### Known limitations

- The Steam connection between two different accounts has not been confirmed (only tested on one machine).
- Hand-played mechanics have not been through long sessions (grab and throw, vacuum, turrets and robots, flamethrower burn, slot machine, rebirth, ending, permanent-upgrade panel).
- Occasional multi-second frame hitches were seen in test runs; a hitch longer than the peer timeout drops that player.
- Only the host saves. No per-player saves or kill attribution.
- Balance defaults (`WorkloadExponent = 0.8`, `PerfCap = 1000`) are estimates and not calibrated against long play.
- The F7 panel does not capture mouse and keyboard input from the game while open.

See the README for the complete list.

[Unreleased]: https://github.com/erencivril/CanEmAll-Coop/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/erencivril/CanEmAll-Coop/releases/tag/v0.1.2
[0.1.1]: https://github.com/erencivril/CanEmAll-Coop/releases/tag/v0.1.1
[0.1.0]: https://github.com/erencivril/CanEmAll-Coop/releases/tag/v0.1.0
