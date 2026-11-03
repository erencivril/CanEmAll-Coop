## What and why

<!-- What does this change, and what problem does it solve? Link the issue if there is one. -->

## How it was tested

<!-- Tick what applies and add detail. For network or gameplay changes please say how many game instances, which roles, and which transport. -->

- [ ] `dotnet build mod/CanEmAllCoop -c Release` has 0 errors
- [ ] `dotnet test mod/CanEmAllCoop.Tests` is green
- [ ] Played in the game: <!-- host/client, Steam or Direct, number of players, what you did -->
- [ ] Not run in the game (explain why)

## Checklist

- [ ] Offline stays vanilla: every new patch is a pass-through when there is no session
- [ ] New Harmony targets are resolved by type and method name, and a missing target disables only that feature
- [ ] Pure logic is in a `*.Pure.cs` file (no Unity, BepInEx or Harmony types) and has a test
- [ ] Host and client behaviour is decided explicitly, and `docs/ARCHITECTURE.md` (hook table, message ids) is updated if a hook or message changed
- [ ] New config keys have a default, a range and a description, and are added to the README table
- [ ] `CHANGELOG.md` has an entry under "Unreleased"
- [ ] The change contains **no game files, game code or assets taken from the game, or memory addresses**

## Not done / known gaps

<!-- Edge cases you did not handle, things you could not test. -->
