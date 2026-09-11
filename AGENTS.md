# AGENTS.md

Runs a renamed dummy process so Discord credits "play a game" quests without
installing the game. `QuestLauncher.ps1` is the whole tool; `README.md` explains
the mechanism. This file is the operating procedure.

## Running a quest

The user names a game, usually with a screenshot of the quest card.

```powershell
.\QuestLauncher.ps1 "Marvel Rivals" -DryRun        # check the pick first
.\QuestLauncher.ps1 "Marvel Rivals" -Minutes 17    # then run it
```

Quests need **15 minutes**; use **17** for margin. Always launch with
`run_in_background: true` — a 17-minute foreground call exceeds the tool
timeout.

Then, ~10 seconds after launch, **verify the process has a window**:

```powershell
Get-Process -Name 'marvel-win64-shipping' | Select Id, MainWindowHandle, MainWindowTitle
```

`MainWindowHandle` must be non-zero. This is the single most important check.

Run multiple quests **sequentially, never in parallel** — Discord credits one
activity at a time. Chain them in one background call with `;`.

## Before starting, tell the user

- **Accept the quest first.** A card showing "Accept Quest" has not been
  accepted, and the 17 minutes will not count. Check their screenshot.
- Discord desktop must stay open for the whole run.

## The four things that will bite you

1. **A windowless process is silently ignored.** A stub that just sleeps has
   `MainWindowHandle: 0` and Discord never sees it — no error, no status,
   nothing. This cost a wasted 17-minute run. The stub is a WinForms app for
   exactly this reason; do not "simplify" it back to a sleep loop.

2. **About half of all games cannot be faked.** 13,355 of 23,799 entries in
   Discord's list register zero executables (Shift at Midnight is one) — those
   are detected through Steam/Xbox SKUs instead. The script exits **2** and
   explains. This is not a bug to fix; say so plainly and don't go forging
   store install state unless the user explicitly asks.

3. **Never pick an executable naively.** Games list launchers, test builds and
   third-party clients — RuneScape registers `osbuddy.exe` next to
   `runescape.exe`. `Select-Executable` scores by resemblance to the game name.
   Use `-DryRun` to confirm before committing 15 minutes.

4. **Exit code 3 means the stub died under 15 minutes** and the quest did not
   count. Re-run it. The script reports actual elapsed time, not the requested
   duration — keep it that way.

## When a quest doesn't progress

Ask one question first: **does Discord show "Playing <game>" at all?**

- **No status** → detection failed. Check `MainWindowHandle` (see above), then
  that the exe name matches, then User Settings → Activity Privacy.
- **Status but no progress** → detection worked; the quest is applying the
  stricter install check (Steam `appmanifest_<appid>.acf` + matching
  `steamapps\common\<installdir>\`). Not implemented here.

Don't jump to the Steam explanation. It looks right and is usually wrong.

## Source of truth

`https://discord.com/api/v9/applications/detectable` — the list Discord
actually matches against. Do **not** use SteamDB depots: the API is
authoritative and gives the required parent folder, which SteamDB won't.

Names containing `/` are path-qualified: `win64/bf6.exe` means the immediate
parent folder must be `win64`, but everything above it is ignored.

## Housekeeping

Runtime state lives in `%LOCALAPPDATA%\DiscordQuestLauncher\` (12 MB game list,
index, compiled stub, generated folders) — outside the repo, safe to delete.
The user expects it cleaned up when a batch of quests is done.

Don't commit without being asked.
