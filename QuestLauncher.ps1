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
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'

$DetectableUrl = 'https://discord.com/api/v9/applications/detectable'
$DataDir       = Join-Path $env:LOCALAPPDATA 'DiscordQuestLauncher'
$RawCache      = Join-Path $DataDir 'detectable.json'
$IndexCache    = Join-Path $DataDir 'index.json'
$StubExe       = Join-Path $DataDir 'stub.exe'
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
    # parse in a fraction of a second.
    $full = Get-Content $RawCache -Raw -Encoding UTF8 | ConvertFrom-Json
    $slim = foreach ($g in $full) {
        $exes = @($g.executables | Where-Object { $_.os -eq 'win32' })
        if ($exes.Count -eq 0) { continue }
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
    Write-Host "Indexed $($slim.Count) Windows-detectable games." -ForegroundColor DarkGray
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

# Games list several binaries (launchers, test builds, crash handlers). Score
# them so we pick the one that represents actually being in-game.
function Get-ExeScore {
    param([string]$Name)

    $n = $Name.ToLowerInvariant()
    $score = 0
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

    return ($candidates |
        Sort-Object -Property @{ Expression = { Get-ExeScore $_.name }; Descending = $true },
                              @{ Expression = { $_.name.Length } } |
        Select-Object -First 1)
}


# --- The dummy executable -----------------------------------------------------

# A ~3.5 KB headless binary that sleeps until killed. Compiled once with the
# C# compiler that ships with the .NET Framework, present on all Win10/11.
function Ensure-Stub {
    if (Test-Path $StubExe) { return $StubExe }

    Write-Host "Building dummy executable (one time)..." -ForegroundColor Cyan
    $src = @'
using System.Threading;
public class QuestStub {
    public static void Main() {
        Thread.Sleep(Timeout.Infinite);
    }
}
'@
    Add-Type -TypeDefinition $src -OutputAssembly $StubExe -OutputType WindowsApplication
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
    Write-Host "$($target.name) has no Windows executable listed, so it can't be faked this way." -ForegroundColor Red
    exit 1
}

# "win64/marvel-win64-shipping.exe" -> folder "win64", file "marvel-win64-shipping.exe"
$relative = $exe.name -replace '/', '\'
$fullPath = Join-Path $BaseDir $relative
$folder   = Split-Path $fullPath -Parent

if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
Copy-Item (Ensure-Stub) -Destination $fullPath -Force

Write-Host ""
Write-Host "Game:    " -NoNewline; Write-Host $target.name -ForegroundColor Green
Write-Host "Process: " -NoNewline; Write-Host $relative -ForegroundColor Green
Write-Host "Path:    $fullPath" -ForegroundColor DarkGray

if (-not (Get-Process -Name 'Discord*' -ErrorAction SilentlyContinue)) {
    Write-Host "Warning: Discord doesn't appear to be running. Start it, or nothing will be detected." -ForegroundColor Yellow
}

$proc = Start-Process -FilePath $fullPath -PassThru
Start-Sleep -Milliseconds 500
if ($proc.HasExited) {
    Write-Host "The dummy process exited immediately -- antivirus may have blocked it." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Running for $Minutes minutes. Press Ctrl+C to stop early." -ForegroundColor Cyan
Write-Host "Check the quest in Discord: User Settings > Gift Inventory." -ForegroundColor DarkGray
Write-Host ""

try {
    $deadline = (Get-Date).AddMinutes($Minutes)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
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

Write-Host ""
Write-Host "Stopped $relative after $Minutes minutes." -ForegroundColor Green
Write-Host "If the quest didn't complete, it may use the stricter Steam check -- see the README." -ForegroundColor DarkGray
