# Testing status (v0.1.0)

This page says what has been verified, how, and what has **not** been verified yet. The mod is a pre-release: everything below was
tested on **one PC with two game instances**. No test used two different PCs or two different Steam accounts.

## How the tests were run

- Game build: Unity 6000.2.15f1 (IL2CPP), BepInEx 6.0.0-be.791.
- Two game instances on one PC: the normal Steam install as the host and a full copy of the game folder as the client, connected over
  Direct TCP on `127.0.0.1`. Both instances shared one save folder, so the saves were backed up before and restored after every run.
- Instead of hands on the keyboard, a debug driver (`Debug/DebugModule.cs`, inactive unless its launch switches are given) called the same
  game methods that a click or a hit ends in: buy a skill, hit and kill enemies, move bodies into the grinder, teleport both players,
  take screenshots. It does nothing in a normal game.
- `[STATE]` lines (every 2 s) and one `[DAYSTATS]` line per day in `BepInEx/LogOutput.log` were compared on both sides.
- 328 unit tests cover all pure logic (protocol, codecs, interpolation, pace math, quorum rules, state machines). They need no game:
  `dotnet test mod/CanEmAllCoop.Tests`.

## Verified in two-instance tests (Direct TCP, one PC)

| Area | Result |
|---|---|
| Offline (no session) | Every module loads, all patches apply, no errors. The pace factor is exactly 1. A scripted run plays day after day like the unmodified game. |
| Join and snapshot | The client connects, receives the host's game state, loads the host's world and runs the host's day. A client that joins in the middle of a day picks up the running day. |
| Shared clock | The day timer on both sides stays within about one second. |
| Shared money and skills | Wallet and unlocked skills are identical on both sides at the same moment, over many purchases (93 client purchases in one run, price deducted once each). |
| Player-count balancing | With two players the host latches `N = 2` at day start and applies `k` of about 1.67 to 1.76. Money before and after the divisor on the host: 764 and 434, a ratio of 0.568, which equals `1 / k`. Leaving players drop `N` back to 1 from the next day. |
| Enemies | Enemies exist only on the host; the client shows matching puppets (209 of 209 at the end of one day). Client hits reach the host and are applied (175 sent, 167 results received in one run); kills and blood show on the client. |
| Cans and grinder | Cans and the reward pipe work with money divided by `k` (driven by moving bodies and cans by script, not by hand). |
| Day flow | Ready-up and the elevator barriers release correctly for several days in a row. Down, respawn and team wipe work. |
| Saves | The client's save is never written while joined. The host's save is written at the end of a day, as in a normal game. |
| Leaving | A client that is killed mid-day disappears on the host, which keeps running. A client rejoins mid-day with a fresh snapshot. When the host quits, the client shows a message and returns to the main menu with its own save intact. |
| Version handshake | A client built with a different mod version is refused. The joiner sees `Version mismatch: host has X, you have Y - install the same mod version`, the host sees `Refused <name>: they have mod version Y, you have X. Both must install the same version.` Both appear as an on-screen message for about 7 seconds. No retry loop. |
| On-screen messages | The message box was checked on screen on both host and client. A full session, a join timeout and a lost connection use the same code path but were not triggered in the game. |
| Remote avatar | The other player is visible as a capsule with a hard hat and a name tag. The name tag is large, has an opaque background and stays on screen. |
| F7 panel | Renders fully (own SteamID64 with Copy, Host (Steam), friends list, Host (Direct), Direct join field). Typing into the text field with real key events works. Not exercised: mouse-wheel scrolling of the friends list, paste, and joining through the panel. |
| Steam API on one machine | The Steam host starts without errors, advertises itself, and leaves cleanly. This does not prove that two accounts can talk to each other. |

### Frame times

Measured over four in-game days with a client skill purchase every 4 seconds and up to 209 puppets, screenshots off:

- Average frame time about 6 ms on both instances (two games on one PC).
- During the day the slowest frame in any 2-second window was **36 ms on the host and 18 ms on the client**. No frame longer than 1 second.
- Only scene loads are long: 1.4 to 1.8 seconds when a game starts or when the client returns to the menu.
- Earlier builds had single frames of several seconds. They went away after queued work was spread over frames; the cause of the remaining one-off hitches seen with screenshots on was not isolated.

Taking 2560x1440 screenshots costs 0.3 to 0.5 seconds of main-thread time per shot, so never judge hitches from a run with screenshots on.

## Not verified yet

1. **Steam between two real accounts.** The exchange of messages over Steam (session acceptance, relay, fragmentation of the join snapshot) has not been confirmed. Direct TCP is the tested path. If you try Steam and it does not connect, please open an issue with the log of both players.
2. **Hand-played mechanics.** Weapon feel, vacuum, grabbing and throwing bodies and cans, turrets and robots, flamethrower burn sync, the slot machine, rebirth, the ending and the permanent-upgrade panel were not played by hand with two people.
3. **3 and 4 players.** Only two players were tested. The numbers for three and four players come from the math and the unit tests.
4. **Late game.** A 750-enemy late game and the bandwidth it needs (calculated at roughly 90 to 115 KB/s per client in the worst case, not measured) were not tested.
5. **Balance over time.** `WorkloadExponent = 0.8` and `PerfCap = 1000` are design estimates that were not compared against a long solo campaign.
6. **Joining by SteamID64 for someone who is not on your friends list.**
7. **Other mods and other game builds.** Tested with one game build and no other BepInEx mods.

The `[DAYSTATS]` log line (one per day on the host) is meant to collect the data for items 3 to 5. Reports with real numbers are the most useful feedback.
