<#
.SYNOPSIS
    Completes Discord "play a game" quests by running a dummy process named
    after the game, instead of installing the real thing.

.DESCRIPTION
    Discord detects games by scanning the running process list and matching it
    against its public game list (/api/v9/applications/detectable). Each entry
    lists the executable name it looks for. When that name is path-qualified
    (e.g. "win64/marvel-win64-shipping.exe") the parent folder must match too,
    but everything above it is ignored -- so any base directory works.

    This script looks the game up in that list, builds the required folder
    structure, drops a tiny do-nothing executable in it under the right name,
    and runs it for the quest duration.

.EXAMPLE
    .\QuestLauncher.ps1 "Marvel Rivals"

.EXAMPLE
    .\QuestLauncher.ps1 "Delta Force" -Minutes 17

.EXAMPLE
    .\QuestLauncher.ps1 fallout -List
#>
[CmdletBinding()]
param(
    # Game name to search for (substring match, case-insensitive).
    [Parameter(Position = 0)]
    [string]$Game,

    # How long to keep the dummy process alive. Most quests need 15 minutes.
    [int]$Minutes = 15,

    # Show matching games and their executables, then exit without running.
    [switch]$List,

    # Disambiguate when several games match, without being prompted.
    [int]$Pick,

    # Where the fake game folders are created. Any location works.
    [string]$BaseDir,

    # Re-download Discord's game list even if the cache is fresh.
    [switch]$Refresh,

    # Leave the dummy files on disk after exiting.
    [switch]$Keep,

    # Show which executable would be impersonated, without running anything.
    [switch]$DryRun,

    # Impersonate a specific registered executable instead of the scored pick.
    # Useful when one of a game's names is known to work and another isn't.
    [string]$Executable
)

$ErrorActionPreference = 'Stop'

$DetectableUrl = 'https://discord.com/api/v9/applications/detectable'
$DataDir       = Join-Path $env:LOCALAPPDATA 'DiscordQuestLauncher'
$RawCache      = Join-Path $DataDir 'detectable.json'
$IndexCache    = Join-Path $DataDir 'index.json'
$StubExe       = Join-Path $DataDir 'stub2.exe'
$CacheMaxAge   = [TimeSpan]::FromHours(24)

if (-not $BaseDir) { $BaseDir = Join-Path $DataDir 'games' }

if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }


# --- Discord's game list ------------------------------------------------------

function Get-GameIndex {
    param([switch]$Force)

    $stale = $true
    if ((-not $Force) -and (Test-Path $IndexCache)) {
        $age = (Get-Date) - (Get-Item $IndexCache).LastWriteTime
        if ($age -lt $CacheMaxAge) { $stale = $false }
    }
    if (-not $stale) {
        return Get-Content $IndexCache -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    Write-Host "Downloading Discord's game list..." -ForegroundColor Cyan
    $prev = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the progress bar
    try {
        Invoke-WebRequest -Uri $DetectableUrl -OutFile $RawCache -UseBasicParsing
    } finally {
        $ProgressPreference = $prev
    }

    # Slim 12 MB of metadata down to just what we match on, so later runs
    # parse in a fraction of a second. Games with no Windows executable are
    # kept deliberately: over half the list is like that, and silently dropping
    # them turns "this game can't be faked" into a misleading "game not found".
    $full = Get-Content $RawCache -Raw -Encoding UTF8 | ConvertFrom-Json
    $slim = foreach ($g in $full) {
        $exes = @($g.executables | Where-Object { $_.os -eq 'win32' })
        [pscustomobject]@{
            name        = $g.name
            id          = $g.id
            executables = @($exes | ForEach-Object {
                [pscustomobject]@{ name = $_.name; is_launcher = [bool]$_.is_launcher }
            })
        }
    }
    $slim = @($slim)
    ($slim | ConvertTo-Json -Depth 6 -Compress) | Out-File $IndexCache -Encoding utf8
    $runnable = @($slim | Where-Object { $_.executables.Count -gt 0 }).Count
    Write-Host "Indexed $($slim.Count) games ($runnable with a Windows executable)." -ForegroundColor DarkGray
    return $slim
}

function Find-Game {
    param($Index, [string]$Query)

    $exact = @($Index | Where-Object { $_.name -eq $Query })
    if ($exact.Count -eq 1) { return $exact }

    $matches = @($Index | Where-Object { $_.name -like "*$Query*" })
    # Shorter names rank higher: "Fallout 4" beats "Fallout 4 VR" for "fallout 4".
    return @($matches | Sort-Object { $_.name.Length }, { $_.name })
}


# --- Choosing which executable to impersonate --------------------------------

function Get-NameKey {
    param([string]$Text)
    return ($Text.ToLowerInvariant() -replace '[^a-z0-9]', '')
}

# Games list several binaries: launchers, test builds, crash handlers, and
# sometimes third-party clients (RuneScape lists osbuddy.exe next to
# runescape.exe). Score them so we pick the one that means actually in-game.
function Get-ExeScore {
    param([string]$Name, [string]$GameName)

    $n = $Name.ToLowerInvariant()
    $score = 0

    # Resemblance to the game's own name is the strongest signal -- it's what
    # separates the official client from a third-party one.
    $base = Get-NameKey ([System.IO.Path]::GetFileNameWithoutExtension($n))
    $game = Get-NameKey $GameName
    if ($base -and $game) {
        if ($base -eq $game) { $score += 50 }
        elseif ($game.Contains($base) -or $base.Contains($game)) { $score += 25 }
    }

    if ($n -match 'shipping')                                  { $score += 30 }
    if ($n -match '/')                                         { $score += 10 }
    if ($n -match 'launcher|bootstrap|updater')                { $score -= 100 }
    if ($n -match 'test|benchmark|editor|server|crash|unins')  { $score -= 50 }
    return $score
}

function Select-Executable {
    param($GameEntry)

    $candidates = @($GameEntry.executables | Where-Object { -not $_.is_launcher })
    if ($candidates.Count -eq 0) { $candidates = @($GameEntry.executables) }
    if ($candidates.Count -eq 0) { return $null }

    $name = $GameEntry.name
    return ($candidates |
        Sort-Object -Property @{ Expression = { Get-ExeScore $_.name $name }; Descending = $true },
                              @{ Expression = { $_.name.Length } } |
        Select-Object -First 1)
}


# --- The dummy executable -----------------------------------------------------

# A small binary that opens a real top-level window and sits there. The window
# matters: a process that merely sleeps has no MainWindowHandle, and Discord
# skips those -- it looks for something that behaves like a running game.
# Compiled once with the C# compiler that ships with the .NET Framework.
function Ensure-Stub {
    if (Test-Path $StubExe) { return $StubExe }

    Write-Host "Building dummy executable (one time)..." -ForegroundColor Cyan
    $src = @'
using System;
using System.Windows.Forms;

public class QuestStub {
    [STAThread]
    public static void Main(string[] args) {
        string title = "Game";
        if (args.Length > 0) { title = args[0]; }

        Form f = new Form();
        f.Text = title;
        f.Width = 420;
        f.Height = 240;
        f.ShowInTaskbar = true;
        f.WindowState = FormWindowState.Minimized;
        Application.Run(f);
    }
}
'@
    Add-Type -TypeDefinition $src -OutputAssembly $StubExe -OutputType WindowsApplication `
             -ReferencedAssemblies 'System.Windows.Forms', 'System.Drawing'
    return $StubExe
}


# --- Main ---------------------------------------------------------------------

if (-not $Game) {
    Write-Host "Usage: .\QuestLauncher.ps1 <game name> [-Minutes 15] [-List]" -ForegroundColor Yellow
    exit 1
}

$index = Get-GameIndex -Force:$Refresh
$found = Find-Game -Index $index -Query $Game

if ($found.Count -eq 0) {
    Write-Host "No game matching '$Game' in Discord's detectable list." -ForegroundColor Red
    Write-Host "Try a shorter query, or -Refresh if the game was added recently." -ForegroundColor DarkGray
    exit 1
}

if ($List) {
    foreach ($g in $found) {
        Write-Host ""
        Write-Host "$($g.name)  " -NoNewline -ForegroundColor Green
        Write-Host "(app id $($g.id))" -ForegroundColor DarkGray
        foreach ($e in $g.executables) {
            $tag = ''
            if ($e.is_launcher) { $tag = '  [launcher]' }
            Write-Host "    $($e.name)$tag"
        }
    }
    Write-Host ""
    exit 0
}

if ($found.Count -gt 1) {
    if ($Pick -ge 1 -and $Pick -le $found.Count) {
        $target = $found[$Pick - 1]
    } else {
        Write-Host "'$Game' matches $($found.Count) games:" -ForegroundColor Yellow
        for ($i = 0; $i -lt $found.Count; $i++) {
            Write-Host ("  [{0}] {1}" -f ($i + 1), $found[$i].name)
        }
        try {
            $answer = Read-Host "Pick a number (or Enter to cancel)"
        } catch {
            Write-Host "Re-run with -Pick <number> to choose." -ForegroundColor DarkGray
            exit 1
        }
        if (-not $answer) { exit 1 }
        $n = 0
        if (-not [int]::TryParse($answer, [ref]$n) -or $n -lt 1 -or $n -gt $found.Count) {
            Write-Host "Not a valid choice." -ForegroundColor Red
            exit 1
        }
        $target = $found[$n - 1]
    }
} else {
    $target = $found[0]
}

$exe = Select-Executable -GameEntry $target
if (-not $exe) {
    Write-Host ""
    Write-Host "$($target.name) registers no Windows executable with Discord." -ForegroundColor Red
    Write-Host "There is no process name to impersonate, so this quest cannot be" -ForegroundColor DarkGray
    Write-Host "completed by faking a process. Discord detects it through the store" -ForegroundColor DarkGray
    Write-Host "it ships on (Steam/Xbox) instead. You'd have to actually install it." -ForegroundColor DarkGray
    exit 2
}

if ($Executable) {
    $wanted = ($Executable.ToLowerInvariant() -replace '\\', '/')
    $exe = @($target.executables | Where-Object {
        $n = $_.name.ToLowerInvariant()
        ($n -eq $wanted) -or ([System.IO.Path]::GetFileName($n) -eq [System.IO.Path]::GetFileName($wanted))
    }) | Select-Object -First 1

    if (-not $exe) {
        Write-Host "$($target.name) does not register '$Executable'. It registers:" -ForegroundColor Red
        foreach ($e in $target.executables) { Write-Host "    $($e.name)" }
        exit 1
    }
}

# "win64/marvel-win64-shipping.exe" -> folder "win64", file "marvel-win64-shipping.exe"
$relative = $exe.name -replace '/', '\'
$fullPath = Join-Path $BaseDir $relative
$folder   = Split-Path $fullPath -Parent

if ($DryRun) {
    Write-Host "$($target.name)  ->  $relative" -ForegroundColor Green
    exit 0
}

if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
Copy-Item (Ensure-Stub) -Destination $fullPath -Force

Write-Host ""
Write-Host "Game:    " -NoNewline; Write-Host $target.name -ForegroundColor Green
Write-Host "Process: " -NoNewline; Write-Host $relative -ForegroundColor Green
Write-Host "Path:    $fullPath" -ForegroundColor DarkGray

if (-not (Get-Process -Name 'Discord*' -ErrorAction SilentlyContinue)) {
    Write-Host "Warning: Discord doesn't appear to be running. Start it, or nothing will be detected." -ForegroundColor Yellow
}

$proc = Start-Process -FilePath $fullPath -ArgumentList "`"$($target.name)`"" -PassThru
Start-Sleep -Milliseconds 1500
if ($proc.HasExited) {
    Write-Host "The dummy process exited immediately -- antivirus may have blocked it." -ForegroundColor Red
    exit 1
}

$proc.Refresh()
if ($proc.MainWindowHandle -eq 0) {
    Write-Host "Warning: the process has no window yet; Discord may not pick it up." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Running for $Minutes minutes. Press Ctrl+C to stop early." -ForegroundColor Cyan
Write-Host "Check the quest in Discord: User Settings > Gift Inventory." -ForegroundColor DarkGray
Write-Host ""

$QuestMinutes = 15     # what Discord actually requires; anything past this is margin
$started      = Get-Date
$diedEarly    = $false

try {
    $deadline = $started.AddMinutes($Minutes)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $diedEarly = $true
            Write-Host "`nThe dummy process died unexpectedly." -ForegroundColor Red
            break
        }
        $left = $deadline - (Get-Date)
        Write-Host ("`r  {0:mm\:ss} remaining " -f $left) -NoNewline
        Start-Sleep -Seconds 1
    }
    Write-Host "`r  Done.                    "
} finally {
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not $Keep) {
        Start-Sleep -Milliseconds 300
        Remove-Item $fullPath -Force -ErrorAction SilentlyContinue
    }
}

# Report what actually happened, not what was asked for -- a stub that dies at
# minute 3 must not print the same cheerful line as one that ran the full time.
$ran = ((Get-Date) - $started).TotalMinutes
Write-Host ""
if ($diedEarly) {
    Write-Host ("$relative only ran {0:N1} of the {1} minutes requested." -f $ran, $Minutes) -ForegroundColor Yellow
    if ($ran -ge $QuestMinutes) {
        Write-Host "That still clears the $QuestMinutes-minute quest requirement, so it likely counted." -ForegroundColor DarkGray
    } else {
        Write-Host "That is under the $QuestMinutes-minute requirement -- run it again." -ForegroundColor Red
        exit 3
    }
} else {
    Write-Host ("Stopped $relative after {0:N1} minutes." -f $ran) -ForegroundColor Green
}
Write-Host "If the quest didn't complete, it may use the stricter Steam check -- see the README." -ForegroundColor DarkGray
