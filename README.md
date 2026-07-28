# Discord Quest Launcher

Completes Discord "play a game" quests by running a tiny do-nothing process
named after the game, instead of downloading a few hundred gigabytes you'll
uninstall an hour later.

```powershell
.\QuestLauncher.ps1 "Marvel Rivals"
```

That's it. The script finds the executable name Discord expects, builds the
folder structure it expects, drops a 3.5 KB stub there, and runs it for 15
minutes.

## Usage

```powershell
.\QuestLauncher.ps1 "Delta Force"            # run for the default 15 minutes
.\QuestLauncher.ps1 "Where Winds Meet" -Minutes 17   # add a safety margin
.\QuestLauncher.ps1 fallout -List            # show matches, don't run anything
.\QuestLauncher.ps1 "ARC Raiders" -Refresh   # re-download Discord's game list
```

| Flag | Meaning |
|---|---|
| `-Minutes <n>` | How long to stay "in game". Default `15`. |
| `-List` | Print matching games and their executables, then exit. |
| `-Pick <n>` | Choose among multiple matches without being prompted. |
| `-BaseDir <path>` | Where the fake game folders go. Default `%LOCALAPPDATA%\DiscordQuestLauncher\games`. |
| `-Keep` | Leave the dummy files on disk afterwards. |
| `-DryRun` | Show which executable would be impersonated, without running it. |
| `-Refresh` | Force a re-download of the game list (cached 24h). |

Double-clicking `run.cmd` prompts for a game name, which avoids PowerShell's
execution-policy prompt.

## How the detection works

Discord scans the running process list and matches it against its public game
list at `https://discord.com/api/v9/applications/detectable` — about 12 MB
covering ~10,400 Windows-detectable games. Each entry names the executable it
looks for. Nothing verifies that the process is the real game; the name is the
check.

The important detail is that **most of those names are path-qualified**
(9,291 of 11,143 Windows entries). Marvel Rivals is listed as:

```
win64/marvel-win64-shipping.exe
```

Discord matches the *tail* of the process path, so the immediate parent folder
has to be named `win64`, but everything above it is ignored. That's why the
widely-shared advice of "make a `Win64` folder on your Desktop" works — the
Desktop part is irrelevant, the `Win64` part isn't.

This is also why the detectable API beats digging through SteamDB depots: it's
the list Discord actually matches against, and it tells you the required parent
folder, which SteamDB won't.

### The games from the well-known Reddit post

Verified against the live API:

| Game | Expected executable | Needs a folder? |
|---|---|---|
| Fallout 4 | `fallout4.exe` | no |
| ARC Raiders | `pioneergame.exe` | no |
| Where Winds Meet | `wwm.exe`, `where winds meet.exe` | no |
| RuneScape | `runescape.exe` | no |
| Delta Force | `win64/deltaforceclient-win64-shipping.exe` | **yes** — `Win64\` |
| Marvel Rivals | `win64/marvel-win64-shipping.exe` | **yes** — `Win64\` |
| Umamusume: Pretty Derby | `umamusumeprettyderby/umamusumeprettyderby.exe` | **yes** — `umamusumeprettyderby\` |

Only the bottom three actually need a parent folder. Putting the Reddit set in
one `Win64` directory works because the folder is required by some and harmless
to the rest.

### The process needs a window

This is the part that isn't obvious, and it cost a wasted 17-minute run to
find. A process that merely *exists* under the right name is **not** enough —
a binary that just sleeps has no `MainWindowHandle`, and Discord ignores it
completely. No error, no status, nothing.

So the stub opens a real top-level window (minimized, titled with the game
name) and runs a message loop. That single change took Where Winds Meet from
"not detected at all" to completing normally.

If a quest ever stalls, check this first:

```powershell
(Get-Process runescape).MainWindowHandle   # 0 means Discord won't see it
```

The useful diagnostic is whether Discord shows "Playing *game*" at all. No
status means detection failed — process shape, exe name, or activity privacy.
Status but no quest progress means detection worked and the *quest* is applying
the stricter check below.

### Picking among several executables

Games list multiple binaries — launchers, test builds, crash handlers, and
sometimes third-party clients. RuneScape is the awkward case: it registers
`osbuddy.exe` and `swiftkit-rs.exe` right next to `runescape.exe`.

The script scores candidates and prefers the one that means *actually
in-game*. Resemblance to the game's own name is weighted highest, since that's
what separates the official client from a third-party one; `shipping` builds
rank up; anything matching `launcher`, `test`, `benchmark`, `editor`, `server`
or `crash` is pushed down; and entries flagged `is_launcher` are skipped
outright, because launchers generally don't earn quest credit.

Use `-DryRun` to see what it picked before committing 15 minutes to it.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Ran the full duration. |
| `1` | Game not found, bad input, or the stub was blocked from starting. |
| `2` | The game registers no Windows executable — this quest can't be faked. |
| `3` | The stub died early and ran **less than 15 minutes**. Run it again. |

The script reports how long it *actually* ran, not how long you asked for. If
the stub dies at minute three you get a warning and exit code 3, not a cheerful
"done". If it dies after minute fifteen it still counts, and it says so.

## Caveats

- **Discord desktop must be running**, and you must have accepted the quest
  first (User Settings → Gift Inventory). The script warns if it can't find
  Discord.
- **Some newer quests use a stricter check.** Beyond the process name, Discord
  may verify the game is genuinely installed via Steam — an
  `appmanifest_<appid>.acf` in `steamapps\` plus a matching
  `steamapps\common\<installdir>\` folder. If a quest doesn't progress despite
  the process running, that's the likely reason. This script does not fabricate
  Steam manifests.
- **Games with no `executables` field can't be faked this way** at all, and
  this is the single biggest limitation: **13,355 of 23,799** entries in
  Discord's list register zero executables. Shift at Midnight is one — Discord
  detects it purely through its Xbox and Steam SKUs, so there is no process
  name to impersonate. Roughly half of all quests are immune to this technique.
  The script exits with code 2 and says so explicitly.
- **Antivirus may quarantine the stub.** It's an unsigned executable that does
  nothing but sleep, which is a mildly suspicious shape. The script detects an
  immediate exit and says so.
- Quest timers are Discord-side. Closing Discord mid-run loses progress.

## Requirements

Windows 10 or 11. No install, no dependencies — the stub is compiled on first
run using the C# compiler bundled with the .NET Framework, which ships with
Windows.

## How it's laid out

```
QuestLauncher.ps1   the whole thing
run.cmd             double-click wrapper
```

Runtime state lives outside the repo in `%LOCALAPPDATA%\DiscordQuestLauncher\`:
the cached game list, the slimmed-down search index, the compiled stub, and the
generated game folders.
