#requires -Version 5.1
<#
    Universe Creator - headless self test (Windows / PowerShell)

    Runs the package self test WITHOUT opening the Unity editor, captures the
    compile log, and prints a plain-language verdict. Use it after installing
    the package, after a Unity or URP upgrade, and before reporting a problem.

    Usage (from anywhere):
        powershell -ExecutionPolicy Bypass -File .\uc_selftest.ps1

    The script finds the project and the editor by itself. Override anything:
        .\uc_selftest.ps1 -ProjectPath "C:\Work\MyGame" -UnityPath "C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe"

    Exit codes
        0  PASS            - compiled clean, every check passed
        1  FAIL            - compiled clean, one or more checks failed
        2  COMPILE ERROR   - Unity never reached the test
        3  ENVIRONMENT     - project, editor or lock problem; nothing was run
        4  NO RESULT       - Unity ran but wrote no verdict (crash, licence, OOM)
#>

[CmdletBinding()]
param(
    # Unity project folder (the one holding Assets and ProjectSettings).
    [string] $ProjectPath,

    # Full path to Unity.exe. Falls back to $env:UNITY_PATH, then the Hub.
    [string] $UnityPath,

    # Where to write the compile + test log. Default: <project>\uc_selftest.log
    [string] $LogFile,

    # Run even when the pre-flight checks complain.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Say    ($m) { Write-Host $m }
function Ok     ($m) { Write-Host "  OK    $m" -ForegroundColor Green }
function Warn   ($m) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
function Bad    ($m) { Write-Host "  ERROR $m" -ForegroundColor Red }
function Rule   ()   { Write-Host ('-' * 68) }

Say ''
Say 'Universe Creator - headless self test'
Rule

# ---------------------------------------------------------------- project ----
# Walk up from this script until a folder holds both Assets and ProjectSettings.
if (-not $ProjectPath) {
    $probe = $PSScriptRoot
    while ($probe) {
        if ((Test-Path (Join-Path $probe 'Assets')) -and (Test-Path (Join-Path $probe 'ProjectSettings'))) {
            $ProjectPath = $probe
            break
        }
        $parent = Split-Path $probe -Parent
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
}

if (-not $ProjectPath) {
    Bad 'Could not find a Unity project above this script.'
    Say '        Pass one explicitly:  .\uc_selftest.ps1 -ProjectPath "C:\path\to\project"'
    exit 3
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
if (-not (Test-Path (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'))) {
    Bad "Not a Unity project: $ProjectPath"
    exit 3
}
Ok "project    $ProjectPath"

# The package must actually be installed, or the -executeMethod cannot resolve.
$entry = Join-Path $ProjectPath 'Assets\UniverseCreator\Core\Editor\Diagnostics\UCSelfTest.cs'
if (-not (Test-Path $entry)) {
    Bad 'Universe Creator is not installed in this project.'
    Say '        Expected: Assets\UniverseCreator\Core\Editor\Diagnostics\UCSelfTest.cs'
    Say '        Import the package first, then run this again.'
    exit 3
}
Ok 'package    Assets\UniverseCreator'

# ----------------------------------------------------------------- editor ----
$version = ''
$verLine = Select-String -LiteralPath (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt') `
                         -Pattern 'm_EditorVersion:' | Select-Object -First 1
if ($verLine) { $version = ($verLine.Line -replace 'm_EditorVersion:', '').Trim() }

if (-not $UnityPath) { $UnityPath = $env:UNITY_PATH }

if (-not $UnityPath) {
    $hubs = @(
        "$env:ProgramFiles\Unity\Hub\Editor",
        "${env:ProgramFiles(x86)}\Unity\Hub\Editor",
        "$env:ProgramFiles\Unity Hub\Editor",
        "C:\Unity\Hub\Editor"
    )
    foreach ($hub in $hubs) {
        if (-not (Test-Path $hub)) { continue }

        $exact = Join-Path $hub "$version\Editor\Unity.exe"
        if ($version -and (Test-Path $exact)) { $UnityPath = $exact; break }

        # No exact match: take the newest installed editor and say so.
        $any = Get-ChildItem -LiteralPath $hub -Directory | Sort-Object Name -Descending |
               ForEach-Object { Join-Path $_.FullName 'Editor\Unity.exe' } |
               Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($any) { $UnityPath = $any; break }
    }
}

if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    Bad 'Could not find Unity.exe.'
    Say '        Pass it:   .\uc_selftest.ps1 -UnityPath "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe"'
    Say '        Or set it:  $env:UNITY_PATH = "<path to Unity.exe>"'
    exit 3
}

Ok "editor     $UnityPath"
if ($version -and ($UnityPath -notlike "*\$version\*")) {
    Warn "the project was made with $version - this editor is a different version"
    Warn 'an upgrade prompt in batch mode can look exactly like a test failure'
}

# -------------------------------------------------------------- pre-flight ----
# Unity holds an exclusive lock on an open project; batch mode would refuse it.
$lock = Join-Path $ProjectPath 'Temp\UnityLockfile'
if (Test-Path $lock) {
    if ($Force) {
        Warn 'the project looks open in the editor - continuing because -Force was passed'
    }
    else {
        Bad 'This project is already open in the Unity editor.'
        Say '        Close it and run again (a second process cannot take the project lock).'
        Say '        Or pass -Force to try anyway.'
        exit 3
    }
}

# A Unity editor open on ANOTHER project is the dangerous case, and the lock
# check above cannot see it - that lock is per project. Two editors on a 16 GB
# machine is roughly 3-4 GB each plus asset import workers and a shader
# compiler, and the loser of that race is whichever one asks for memory next.
# It is not always this one: a batch editor started here has killed a person's
# unrelated editor session mid-edit. So this refuses to start rather than
# gamble with work that is not ours.
$others = @()
try {
    $others = @(Get-Process Unity -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path -like '*\Editor\Unity.exe' })
}
catch { }

if ($others.Count -gt 0) {
    Bad "A Unity editor is already running ($($others.Count) process(es))."
    foreach ($o in $others) {
        # The project is on the editor's own command line, so it can be named
        # instead of leaving the user to guess which window is at risk.
        $proj = ''
        try {
            $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$($o.Id)").CommandLine
            if ($cl -match '-projectpath\s+"?([^"]+?)"?(\s+-|\s*$)') { $proj = $Matches[1].Trim() }
        }
        catch { }
        if (-not $proj) { $proj = '(project unknown)' }
        Say "        pid $($o.Id)  $proj"
    }
    Say ''
    Say '        A batch editor started now competes with that session for RAM,'
    Say '        and the one that dies may be the editor you have open - not this test.'
    Say '        Close it and run again, or pass -Force to accept that risk.'
    if (-not $Force) { exit 3 }
    Warn 'continuing anyway because -Force was passed'
}

# A mid-test crash on a busy machine is a memory problem, not a code problem.
# This aborts rather than warns: an out-of-memory kill part way through looks
# exactly like a failing check, and the log it leaves behind is misleading.
try {
    $freeGb = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
    if ($freeGb -lt 3.0) {
        Bad "only $freeGb GB RAM free - a batch editor needs roughly 3-4 GB"
        Say '        Close other applications and run again, or pass -Force.'
        Say '        Starting anyway usually ends in an abort that reads as a test failure.'
        if (-not $Force) { exit 3 }
        Warn 'continuing anyway because -Force was passed'
    }
    else { Ok "memory     $freeGb GB free" }
}
catch { }

if (-not $LogFile) { $LogFile = Join-Path $ProjectPath 'uc_selftest.log' }
Ok "log        $LogFile"

# ------------------------------------------------------------------- run -----
Rule
Say 'Running. A first run compiles the whole project and can take several minutes.'
Say ''

$method = 'UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch'

# One pre-quoted string, never an array: PowerShell 5.1 joins array elements
# without quoting them, so any path with a space arrives at Unity split in two.
$argList = '-batchmode -quit -nographics' +
           ' -projectPath "' + $ProjectPath + '"' +
           ' -logFile "'     + $LogFile     + '"' +
           ' -executeMethod ' + $method

$started = Get-Date
$proc    = Start-Process -FilePath $UnityPath -ArgumentList $argList -PassThru -Wait
$elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

Say ("Editor exited after $elapsed s (exit code " + $proc.ExitCode + ').')
Say ''

# --------------------------------------------------------------- verdict -----
Rule
Say 'RESULT'
Rule

if (-not (Test-Path -LiteralPath $LogFile)) {
    Bad 'No log was written, so Unity never really started.'
    Say ''
    Say 'Most likely causes, in order:'
    Say '  1. Another Unity process holds the project lock -> close the editor.'
    Say '  2. The editor is not activated -> open it once by hand and sign in.'
    Say '  3. The log folder is not writable -> pass -LogFile "$env:TEMP\uc_selftest.log".'
    exit 4
}

$log = Get-Content -LiteralPath $LogFile

$csErrors = @($log | Where-Object { $_ -match 'error CS[0-9]{4}' })
if ($csErrors.Count -gt 0) {
    # WHICH ASSET IS BROKEN MATTERS MORE THAN HOW MANY ERRORS THERE ARE.
    # Unity compiles the whole project, so ONE broken script anywhere stops this
    # test from running at all. Reported as a flat list, that reads as "Universe
    # Creator does not compile" even when every error is in an unrelated asset -
    # and the reader then spends their time on the wrong folder. So the lines are
    # split by whose file they are in before anything is said about the cause.
    $ours   = @($csErrors | Where-Object { $_ -match 'Assets[\\/]UniverseCreator[\\/]' })
    $theirs = @($csErrors | Where-Object { $_ -notmatch 'Assets[\\/]UniverseCreator[\\/]' })

    if ($ours.Count -eq 0) {
        Bad "COMPILE ERROR - but NOT in Universe Creator. The test never ran."
        Say ''
        Say '    Every C# error is in another script in this project. Unity compiles'
        Say '    everything together, so one broken file anywhere stops this test.'
        Say '    Nothing here points at Universe Creator.'
        Say ''
        $theirs | Select-Object -First 20 | ForEach-Object { Say "    $_" }
        if ($theirs.Count -gt 20) { Say "    ... and $($theirs.Count - 20) more (see the log)" }
        Say ''
        Say '    Fix those scripts (or remove them) and run this again.'
        Say ''
        Say "Log: $LogFile"
        exit 2
    }

    Bad "COMPILE ERROR - $($ours.Count) in Universe Creator, $($theirs.Count) elsewhere. The test never ran."
    Say ''
    Say '    In Universe Creator:'
    $ours | Select-Object -First 20 | ForEach-Object { Say "    $_" }
    if ($ours.Count -gt 20) { Say "    ... and $($ours.Count - 20) more (see the log)" }

    if ($theirs.Count -gt 0) {
        Say ''
        Say '    In other assets (listed so they are not mistaken for ours):'
        $theirs | Select-Object -First 10 | ForEach-Object { Say "    $_" }
        if ($theirs.Count -gt 10) { Say "    ... and $($theirs.Count - 10) more (see the log)" }
    }

    Say ''
    Say 'What this usually means:'
    Say '  - Another asset in the project declares a type this package also uses.'
    Say '  - The package was imported into a Unity version older than it needs.'
    Say '  - Only part of the package was imported (a partial or interrupted import).'
    Say ''
    Say "Log: $LogFile"
    exit 2
}
Ok 'compile    no C# errors'

# Two lines in every batch log read like faults and are not. Naming them here
# stops a clean run from looking broken, and stops a real fault from hiding
# behind "oh, that one is normal".
$transport = @($log | Where-Object { $_ -match 'OnTransportError' })
if ($transport.Count -gt 0) {
    $closing = @($log | Where-Object { $_ -match 'Editor is closing down|requested this worker to shutdown' })
    if ($closing.Count -gt 0) {
        Ok 'workers    import worker transport error is the normal shutdown handshake'
    }
    else {
        Warn 'an import worker reported a transport error with no shutdown request before it'
        Warn 'that one is real - the worker died rather than being asked to stop'
    }
}

$accel = @($log | Where-Object { $_ -match 'AcceleratorClientConnectionCallback|Curl error 3' })
if ($accel.Count -gt 0) {
    Warn 'the asset cache server is set to "use preferences" with no endpoint configured'
    Warn 'set Editor Settings > Cache Server to Disabled to silence it (it caches nothing as is)'
}

# The verdict block RunBatch writes: header, checks run, failures, then any
# failure lines. Print it verbatim - it is the artefact support asks for.
$hit = $log | Select-String -Pattern '=== UC SELF TEST' | Select-Object -First 1
if (-not $hit) {
    Bad 'The project compiled, but the self test produced no verdict.'
    Say ''

    $signatures = @(
        @{ Pattern = 'Multiple Unity instances';        Hint = 'another Unity process was running - close the editor and retry' },
        @{ Pattern = 'LICENSE SYSTEM|Unity Editor licen|No valid Unity Editor licen';
                                                        Hint = 'the editor is not activated - open Unity once by hand and sign in' },
        @{ Pattern = 'out of memory|Could not allocate memory';
                                                        Hint = 'the machine ran out of RAM mid-test - close other apps and retry' },
        @{ Pattern = 'executeMethod.*could not be found|Couldn.t find method';
                                                        Hint = 'the editor assembly did not load - reimport Assets/UniverseCreator' },
        @{ Pattern = 'Crash!!!|Fatal Error|Unhandled Exception';
                                                        Hint = 'the editor crashed - the log holds the stack above this line' }
    )

    foreach ($s in $signatures) {
        $found = @($log | Where-Object { $_ -match $s.Pattern })
        if ($found.Count -gt 0) {
            Warn $s.Hint
            Say "        first match: $($found[0])"
        }
    }

    Say ''
    Say "Log: $LogFile"
    exit 4
}

$from  = [Math]::Max(0, $hit.LineNumber - 1)
$block = $log[$from .. [Math]::Min($log.Length - 1, $from + 24)]

Say ''
foreach ($line in $block) {
    if ($line -match '^\s*$') { break }
    Say "    $line"
}
Say ''

if ($hit.Line -match 'PASS') {
    Ok 'self test  PASS'
    Say ''
    Say 'The install is sound: the package compiles and every check agrees.'
    Say 'Anything you see wrong in the scene after this is a setting, not a broken install.'
    Say ''
    Say "Log: $LogFile"
    exit 0
}

Bad 'self test  FAIL'
Say ''
Say 'The package compiled but a check disagreed. The FAIL lines above name the area.'
Say 'Send this log with your report - it is the whole diagnosis:'
Say "    $LogFile"
exit 1
