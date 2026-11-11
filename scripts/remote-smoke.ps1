# Smoke test of the tray in the logged-on user's session. Run it on the Windows PC after
# scripts/remote-test.sh has built bin\ (it also copies this script):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\scripts\remote-smoke.ps1 [-CleanupOnly]
# The tray runs only with a test ini (bin\FakeJVLinkServer.exe on port 18775) through a temporary
# scheduled task (Interactive). The script never starts, stops or kills anything else; the real server
# is only read (GET /health) before and after. -CleanupOnly removes what an interrupted run left behind.
# Step (8) plays a sign-out to the test tray only: WM_QUERYENDSESSION / WM_ENDSESSION sent to its own
# .NET SystemEvents window (cancelled first, then for real).
# Before it starts, the script only looks: if an earlier run left a task, processes or a guard ini behind,
# it stops and asks for -CleanupOnly. It removes a guard ini only when a smoke run wrote it (first line).
param([switch]$CleanupOnly)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false) } catch { }

$Root = Split-Path -Parent $PSScriptRoot
$Bin = Join-Path $Root 'bin'
$Tray = Join-Path $Bin 'JVLinkServerTray.exe'
$Fake = Join-Path $Bin 'FakeJVLinkServer.exe'
$Work = Join-Path $Root 'smoke'
$Logs = Join-Path $Work 'smoke-logs'
$Ini = Join-Path $Work 'smoke.ini'
$GuardIni = Join-Path $Bin 'JVLinkServerTray.ini'   # if --config were ignored, the tray would still read the test ini
$Probe = Join-Path $Work 'probe.ps1'
$ProbeOut = Join-Path $Work 'probe.txt'
$Port = 18775
$LivePort = 8765
$TaskName = 'JVLinkServerTray-smoke'
$Marker = '; smoke test only'   # the first line of every ini this script writes

$P_Start = '起動: JVLinkServer Tray '
$P_Spawned = '起動: サーバーを起動しました'
$P_Adopt = '引き継ぎ: '
$P_Red = '色: \S+ → 赤'
$P_Green = '色: (\S+ → )?緑'
$P_Restart = '起こし直し: '
$P_Ending = '終了の知らせ: サインアウトが始まりました'
$P_Ended = '終了: サインアウトです。アイコンを消して終わります'
$P_Stop1 = '停止 1/4: '
$P_Icon = 'アイコン: 描きました（'
$P_IconAware = 'アイコン: 描きました（\d+ px、.+、PerMonitorV2）'

$script:Failed = $false
$script:Started = $false   # set once the checks before the run passed; nothing is touched before that

function Say([string]$text) { Write-Output $text }

function Step([string]$n, [bool]$ok, [string]$detail) {
    if ($ok) { $tag = 'OK' } else { $tag = 'NG'; $script:Failed = $true }
    Say ('({0}) {1}: {2}' -f $n, $tag, $detail)
}

function Wait-Until([scriptblock]$cond, [double]$seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        if (& $cond) { return $true }
        if ($sw.Elapsed.TotalSeconds -ge $seconds) { return [bool](& $cond) }
        Start-Sleep -Milliseconds 250
    }
}

# Processes are ours only when their full path is bin\JVLinkServerTray.exe or bin\FakeJVLinkServer.exe.
function Procs([string]$name, [string]$path) {
    @(Get-CimInstance Win32_Process -Filter "Name='$name'" | Where-Object {
        $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $path) })
}
function Trays { Procs 'JVLinkServerTray.exe' $Tray }
function Fakes { Procs 'FakeJVLinkServer.exe' $Fake }
function Supervisors { @(Fakes | Where-Object { $_.CommandLine -notmatch '--mode\s+worker' }) }
function Workers { @(Fakes | Where-Object { $_.CommandLine -match '--mode\s+worker' }) }

function Kill-Ours {
    foreach ($p in @(Trays) + @(Fakes)) {   # trays first, so none restarts a fake
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { }
    }
}

function Remove-SmokeTask {
    $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($t) {
        try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch { }
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
}

function Cleanup {
    Remove-SmokeTask
    for ($i = 0; $i -lt 3; $i++) {
        Kill-Ours
        if (Wait-Until { (@(Trays).Count + @(Fakes).Count) -eq 0 } 5) { break }
    }
    Remove-GuardIni
}

# Only a guard ini that a smoke run wrote (its first line is the marker) is ours to remove.
function Remove-GuardIni {
    if (-not (Test-Path -LiteralPath $GuardIni)) { return }
    if ((Get-Content -LiteralPath $GuardIni -TotalCount 1 -Encoding UTF8) -eq $Marker) { Remove-Item -LiteralPath $GuardIni -Force }
    else { Say "left $GuardIni in place: the smoke test did not write it" }
}

function Read-TrayLog {
    if (-not (Test-Path -LiteralPath $Logs)) { return }
    foreach ($f in @(Get-ChildItem -LiteralPath $Logs -Filter 'JVLinkServerTray_*.log' | Sort-Object Name)) {
        $fs = New-Object IO.FileStream($f.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $text = (New-Object IO.StreamReader($fs, (New-Object Text.UTF8Encoding($false)))).ReadToEnd()
        } finally { $fs.Dispose() }
        $text -split "`r?`n" | Where-Object { $_ -ne '' }
    }
}
function Log-Count { @(Read-TrayLog).Count }
function New-Lines([int]$from) { @(Read-TrayLog) | Select-Object -Skip $from }

# The first lines (after $from) that match the patterns in this order, or $null.
function In-Order([int]$from, [string[]]$patterns) {
    $lines = @(New-Lines $from)
    $found = @()
    $i = 0
    foreach ($p in $patterns) {
        while ($i -lt $lines.Count -and $lines[$i] -notmatch $p) { $i++ }
        if ($i -ge $lines.Count) { return $null }
        $found += $lines[$i]
        $i++
    }
    return ,$found
}
function Has([int]$from, [string]$pattern) { @(New-Lines $from | Where-Object { $_ -match $pattern }).Count -gt 0 }
function LineTime([string]$line) {
    [datetime]::ParseExact($line.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
}
function Gap([string]$a, [string]$b) { [int]((LineTime $b) - (LineTime $a)).TotalSeconds }

function Get-Health([int]$port) {
    try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri "http://127.0.0.1:$port/health").Content } catch { $null }
}
function Live-Summary {
    $body = Get-Health $LivePort
    if (-not $body) { return $null }
    $j = $body | ConvertFrom-Json
    [pscustomobject]@{ Status = $j.status; Restarts = $j.components.worker_process.restart_count; Uptime = $j.service.uptime }
}

function Register-Smoke([string]$exe, [string]$arguments, [string]$dir) {
    $a = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $dir
    $pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" -LogonType Interactive
    $st = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskName $TaskName -Action $a -Principal $pr -Settings $st -Force | Out-Null
}
function Register-Tray { Register-Smoke $Tray ('--config "' + $Ini + '"') $Bin }
function Wait-TaskIdle { Wait-Until { (Get-ScheduledTask -TaskName $TaskName).State -ne 'Running' } 20 | Out-Null }
function Task-Info {
    $i = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($i) { 'task state ' + (Get-ScheduledTask -TaskName $TaskName).State + ', last result 0x' + ('{0:X}' -f $i.LastTaskResult) } else { 'no task' }
}

Add-Type -Namespace SmokeNative -Name Kernel32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
'@
function In-Job([int]$procId) {
    try {
        $p = Get-Process -Id $procId -ErrorAction Stop
        $r = $false
        if ([SmokeNative.Kernel32]::IsProcessInJob($p.Handle, [IntPtr]::Zero, [ref]$r)) { if ($r) { 'yes' } else { 'no' } } else { '?' }
    } catch { '?' }
}

function Memory([int]$procId) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $p) { return '?' }
    '{0:N1} MB working set, {1:N1} MB private, {2} handles, {3} threads' -f ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $p.HandleCount, $p.Threads.Count
}

# ------------------------------------------------------------------------------------------------

if ($CleanupOnly) {
    Cleanup
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Say ('cleanup: trays ' + @(Trays).Count + ', fakes ' + @(Fakes).Count + ', task ' + [bool]$task + ', guard ini ' + (Test-Path -LiteralPath $GuardIni))
    exit 0
}

$live0 = $null
try {
    if (-not (Test-Path -LiteralPath $Tray) -or -not (Test-Path -LiteralPath $Fake)) { throw 'build first: bin\JVLinkServerTray.exe and bin\FakeJVLinkServer.exe are needed' }
    if (Test-Path -LiteralPath $GuardIni) { throw "unexpected $GuardIni (a smoke run left it: run -CleanupOnly; otherwise move it away)" }
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw "task $TaskName from an earlier run is there (run -CleanupOnly)" }
    if (@(Trays).Count + @(Fakes).Count -ne 0) { throw 'tray or fake processes from bin\ are running (an earlier run? run -CleanupOnly)' }
    if (Get-Health $Port) { throw "something already answers on port $Port" }
    $script:Started = $true
    $live0 = Live-Summary
    if ($live0) { Say ("live $LivePort before: status $($live0.Status), restart_count $($live0.Restarts), uptime $($live0.Uptime) s (read only)") }
    else { Say "live $LivePort before: no answer (read only)" }

    if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
    New-Item -ItemType Directory -Path $Logs -Force | Out-Null
    $iniText = "$Marker`r`nServerExe=$Fake`r`nServerArgs=--port $Port --log-dir `"$Logs`"`r`n" +
        "PollSeconds=2`r`nUnreachableLimit=3`r`nStartupGraceSeconds=6`r`nShutdownTimeoutSeconds=5`r`n"
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Ini, $iniText, $utf8)
    [IO.File]::WriteAllText($GuardIni, $iniText, $utf8)

    # (1) start through the interactive task
    Register-Tray
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-ScheduledTask -TaskName $TaskName
    Wait-Until { $script:m1 = In-Order 0 @($P_Start, $P_Spawned, $P_Green); [bool]$script:m1 } 10 | Out-Null
    $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $trays = @(Trays)
    if ($trays.Count -eq 0) { throw ('BLOCKED: the tray did not start in the logged-on session (' + (Task-Info) + ')') }
    $tray1 = $trays[0].ProcessId
    $session = (Get-Process -Id $tray1).SessionId
    $sups = @(Supervisors); $wks = @(Workers)
    $startLine = @(New-Lines 0 | Where-Object { $_ -match $P_Start }) | Select-Object -First 1
    $usesIni = [bool]$startLine -and $startLine.Contains($Ini)
    $iconLine = @(New-Lines 0 | Where-Object { $_ -match $P_Icon }) | Select-Object -First 1
    $iconOk = [bool]$iconLine -and $iconLine -match $P_IconAware   # the manifest made the tray per-monitor DPI aware
    $ok = [bool]$script:m1 -and $trays.Count -eq 1 -and $session -ne 0 -and $sups.Count -eq 1 -and $wks.Count -eq 1 -and
        $sups[0].ParentProcessId -eq $tray1 -and $usesIni -and $iconOk
    Step 1 $ok ("起動 and 緑 after $secs s; tray pid $tray1 in session $session; fakes: supervisor pid " +
        ($sups | ForEach-Object { "$($_.ProcessId) (parent $($_.ParentProcessId))" }) + ", worker pid " + ($wks | ForEach-Object { $_.ProcessId }) +
        "; ini from the log matches: $usesIni; icon per-monitor DPI aware: $iconOk (" + $(if ($iconLine) { $iconLine.Substring(20) } else { 'no icon line' }) +
        "); windows: checked in the user's session after (4)")
    if (-not $ok) { throw 'step 1 failed' }

    # (2) kill the fake supervisor
    $n = Log-Count
    $oldSup = $sups[0].ProcessId; $oldWorker = $wks[0].ProcessId
    Stop-Process -Id $oldSup -Force
    Wait-Until { $script:m2 = In-Order $n @($P_Red, $P_Restart, $P_Spawned, $P_Green); [bool]$script:m2 } 35 | Out-Null
    $sups = @(Supervisors); $wks = @(Workers)
    if ($script:m2) {
        $delay = Gap $script:m2[0] $script:m2[1]
        $ok = $delay -ge 8 -and $delay -le 15 -and $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ProcessId -ne $oldSup -and
            $wks[0].ProcessId -ne $oldWorker -and $sups[0].ParentProcessId -eq $tray1
        Step 2 $ok ("killed supervisor $oldSup -> red, restarted $delay s later, green; new supervisor $($sups[0].ProcessId), worker $($wks[0].ProcessId) (orphan worker $oldWorker cleared)")
    } else {
        Step 2 $false 'no red -> restart -> green sequence in the log within 35 s'
    }

    # (3) POST /shutdown (the second restart waits 30 s: backoff)
    $n = Log-Count
    $oldSup = $sups[0].ProcessId
    try { Invoke-WebRequest -UseBasicParsing -Method Post -TimeoutSec 5 -Uri "http://127.0.0.1:$Port/shutdown" | Out-Null } catch { }
    Wait-Until { $script:m3 = In-Order $n @($P_Red, $P_Restart, $P_Spawned, $P_Green); [bool]$script:m3 } 55 | Out-Null
    $sups = @(Supervisors); $wks = @(Workers)
    if ($script:m3) {
        $delay = Gap $script:m3[0] $script:m3[1]
        $ok = $delay -ge 28 -and $delay -le 35 -and $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ProcessId -ne $oldSup
        Step 3 $ok ("POST /shutdown -> red, restarted $delay s later (backoff 30 s for the 2nd restart), green; supervisor $($sups[0].ProcessId), worker $($wks[0].ProcessId)")
    } else {
        Step 3 $false 'no red -> restart -> green sequence in the log within 55 s'
    }

    # (4) kill only the tray
    $sup4 = $sups[0].ProcessId; $wk4 = $wks[0].ProcessId
    $mem1 = Memory $tray1
    Stop-Process -Id $tray1 -Force
    $gone = Wait-Until { @(Trays).Count -eq 0 } 10
    Start-Sleep -Seconds 4
    $sups = @(Supervisors); $wks = @(Workers)
    $alive = $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ProcessId -eq $sup4 -and $wks[0].ProcessId -eq $wk4
    $healthy = [bool](Get-Health $Port)
    Step 4 ($gone -and $alive -and $healthy) ("tray $tray1 killed (gone: $gone); fakes still running: supervisor $sup4 and worker $wk4 = $alive; /health on $Port answers: $healthy")

    # (1, windows) in the user's session: a process here (SSH) cannot see that session's windows.
    Wait-TaskIdle
    $probeText = @'
$fake = '__FAKE__'
$me = (Get-Process -Id $PID).SessionId
$vis = @(Get-Process | Where-Object { $_.SessionId -eq $me -and $_.MainWindowHandle -ne 0 })
$lines = @("session=$me windows=$($vis.Count)")
foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='FakeJVLinkServer.exe'" | Where-Object { $_.ExecutablePath -ieq $fake })) {
    $gp = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
    if ($gp) { $lines += "fake pid=$($p.ProcessId) window=$($gp.MainWindowHandle)" }
}
[IO.File]::WriteAllLines('__OUT__', $lines)
'@
    [IO.File]::WriteAllText($Probe, $probeText.Replace('__FAKE__', $Fake).Replace('__OUT__', $ProbeOut), $utf8)
    Register-Smoke 'powershell.exe' ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $Probe + '"') $Work
    Start-ScheduledTask -TaskName $TaskName
    $got = Wait-Until { Test-Path -LiteralPath $ProbeOut } 30
    Wait-TaskIdle
    Register-Tray
    if ($got) {
        Start-Sleep -Milliseconds 300
        $probe = @(Get-Content -LiteralPath $ProbeOut)
        $head = $probe[0]
        $fakeLines = @($probe | Select-Object -Skip 1)
        $sessionOk = $head -match 'session=(\d+) windows=(\d+)' -and [int]$Matches[1] -ne 0 -and [int]$Matches[2] -gt 0
        $noWindow = $fakeLines.Count -eq 2 -and @($fakeLines | Where-Object { $_ -notmatch 'window=0$' }).Count -eq 0
        Step '1w' ($sessionOk -and $noWindow) ("in the user's session ($head): " + ($fakeLines -join '; '))
    } else {
        Step '1w' $false ('the probe did not run in the user''s session (' + (Task-Info) + ')')
    }

    # (5) run the task again: the tray adopts the running fakes
    $n = Log-Count
    Start-ScheduledTask -TaskName $TaskName
    Wait-Until { $script:m5 = In-Order $n @($P_Start, $P_Adopt, $P_Green); [bool]$script:m5 } 15 | Out-Null
    Start-Sleep -Seconds 6
    $trays = @(Trays); $sups = @(Supervisors); $wks = @(Workers)
    $tray2 = if ($trays.Count -eq 1) { $trays[0].ProcessId } else { 0 }
    $spawned = Has $n $P_Spawned
    $ok = [bool]$script:m5 -and $trays.Count -eq 1 -and $sups.Count -eq 1 -and $sups[0].ProcessId -eq $sup4 -and $wks.Count -eq 1 -and -not $spawned
    $adoptLine = if ($script:m5) { $script:m5[1].Substring(20) } else { '(none)' }
    Step 5 $ok ("tray pid ${tray2}: '$adoptLine'; supervisors: " + $sups.Count + " (pid " + ($sups | ForEach-Object { $_.ProcessId }) + "), started a server: $spawned")
    Say ("memory: first tray after ~2 min: $mem1; second tray after ~10 s: " + (Memory $tray2))

    # (6) let this task's tray start its own server, then end the task
    $n = Log-Count
    $oldSup = $sup4
    try { Invoke-WebRequest -UseBasicParsing -Method Post -TimeoutSec 5 -Uri "http://127.0.0.1:$Port/shutdown" | Out-Null } catch { }
    Wait-Until { $script:m6 = In-Order $n @($P_Red, $P_Restart, $P_Spawned, $P_Green); [bool]$script:m6 } 35 | Out-Null
    $sups = @(Supervisors); $wks = @(Workers)
    $own = [bool]$script:m6 -and $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ParentProcessId -eq $tray2
    if (-not $own) {
        Step 6 $false 'the second tray did not restart the fake server itself'
    } else {
        $sup6 = $sups[0].ProcessId; $wk6 = $wks[0].ProcessId
        $jobs = "in a job: tray " + (In-Job $tray2) + ", supervisor " + (In-Job $sup6) + ", worker " + (In-Job $wk6)
        Stop-ScheduledTask -TaskName $TaskName
        $trayGone = Wait-Until { @(Trays).Count -eq 0 } 15
        Start-Sleep -Seconds 4
        $sups = @(Supervisors); $wks = @(Workers)
        $survived = $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ProcessId -eq $sup6 -and $wks[0].ProcessId -eq $wk6
        $result = if ($survived) { 'the fake server SURVIVED (supervisor ' + $sup6 + ', worker ' + $wk6 + ' still running; /health answers: ' + [bool](Get-Health $Port) + ')' }
                  else { 'the fake server was ENDED WITH THE TRAY (supervisors left ' + $sups.Count + ', workers left ' + $wks.Count + ')' }
        Step 6 $trayGone ("this tray (pid $tray2) restarted the server itself (supervisor $sup6); $jobs; Stop-ScheduledTask ended the tray: $trayGone; " + $result)
    }

    # (8) sign-out as Windows tells it, in the user's session: a probe starts a tray (it adopts the fakes left
    # by (6)), sends WM_QUERYENDSESSION + WM_ENDSESSION(0) = cancelled (the tray must stay), then
    # WM_QUERYENDSESSION + WM_ENDSESSION(1) = the session ends (the tray must go, the server must stay).
    if (-not $own -or @(Supervisors).Count -ne 1) {
        Step 8 $false 'skipped: no fake server left by (6)'
    } else {
        Wait-TaskIdle
        $n = Log-Count
        $sup8 = @(Supervisors)[0].ProcessId; $wk8 = @(Workers)[0].ProcessId
        $endText = @'
$out = '__OUT__'
$lines = @()
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SmokeWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    // The .NET SystemEvents window of a process: it turns these messages into SessionEnding / SessionEnded.
    public static IntPtr EventWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != (uint)pid) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith(".NET-BroadcastEventWindow", StringComparison.Ordinal)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string Send(IntPtr h, uint msg, long w, long l) {
        IntPtr r;
        IntPtr ok = SendMessageTimeout(h, msg, new IntPtr(w), new IntPtr(l), 2, 5000, out r);
        return ok == IntPtr.Zero ? "failed" : r.ToInt64().ToString();
    }
}
"@
    $logoff = 0x80000000L
    $tray = Start-Process -FilePath '__TRAY__' -ArgumentList '--config "__INI__"' -WorkingDirectory '__BIN__' -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 80 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $h = [SmokeWin]::EventWindow($tray.Id) }
    $lines += "tray=$($tray.Id) window=$h"
    Start-Sleep -Seconds 5
    $lines += 'query=' + [SmokeWin]::Send($h, 0x11, 0, $logoff)
    $lines += 'cancelled=' + [SmokeWin]::Send($h, 0x16, 0, $logoff)
    Start-Sleep -Seconds 3
    $tray.Refresh()
    $lines += 'alive-after-cancel=' + (-not $tray.HasExited)
    $lines += 'query2=' + [SmokeWin]::Send($h, 0x11, 0, $logoff)
    $lines += 'ended=' + [SmokeWin]::Send($h, 0x16, 1, $logoff)
    $lines += 'gone-after-end=' + $tray.WaitForExit(10000)
} catch {
    $lines += 'error=' + $_.Exception.Message
}
[IO.File]::WriteAllLines($out, $lines)
'@
        $EndProbe = Join-Path $Work 'end-probe.ps1'
        $EndOut = Join-Path $Work 'end-probe.txt'
        [IO.File]::WriteAllText($EndProbe, $endText.Replace('__OUT__', $EndOut).Replace('__TRAY__', $Tray).Replace('__INI__', $Ini).Replace('__BIN__', $Bin), $utf8)
        Register-Smoke 'powershell.exe' ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $EndProbe + '"') $Work
        Start-ScheduledTask -TaskName $TaskName
        $got = Wait-Until { Test-Path -LiteralPath $EndOut } 60
        Wait-TaskIdle
        if (-not $got) {
            Step 8 $false ('the probe did not run in the user''s session (' + (Task-Info) + ')')
        } else {
            Start-Sleep -Milliseconds 300
            $probe = @(Get-Content -LiteralPath $EndOut)
            $stayed = @($probe | Where-Object { $_ -eq 'alive-after-cancel=True' }).Count -eq 1
            $went = @($probe | Where-Object { $_ -eq 'gone-after-end=True' }).Count -eq 1
            $logged = (Has $n $P_Ending) -and (Has $n $P_Ended)
            $noStop = -not (Has $n $P_Stop1)
            $sups = @(Supervisors); $wks = @(Workers)
            $kept = $sups.Count -eq 1 -and $wks.Count -eq 1 -and $sups[0].ProcessId -eq $sup8 -and $wks[0].ProcessId -eq $wk8 -and [bool](Get-Health $Port)
            Step 8 ($stayed -and $went -and $logged -and $noStop -and $kept -and @(Trays).Count -eq 0) ("probe: " + ($probe -join '; ') +
                "; logged SessionEnding and SessionEnded: $logged; did not stop the server: $noStop; fake supervisor $sup8 and worker $wk8 still running and answering: $kept")
        }
    }
} catch {
    Step 'x' $false ("aborted: " + $_.Exception.Message)
} finally {
    if ($script:Started) {
        # (7) clean up and check
        try { Cleanup } catch { Say ('cleanup error: ' + $_.Exception.Message) }
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        $left = @(Trays).Count + @(Fakes).Count
        $live1 = Live-Summary
        $liveOk = $false
        $liveText = 'no answer'
        if ($live1) {
            $liveText = "status $($live1.Status), restart_count $($live1.Restarts), uptime $($live1.Uptime) s"
            $liveOk = $live1.Status -eq 'healthy'
            if ($live0) { $liveOk = $liveOk -and $live1.Restarts -eq $live0.Restarts -and $live1.Uptime -ge $live0.Uptime }
        }
        Step 7 ($left -eq 0 -and -not $task -and -not (Test-Path -LiteralPath $GuardIni) -and $liveOk) ("left: trays+fakes $left, task " + [bool]$task + ", guard ini " +
            (Test-Path -LiteralPath $GuardIni) + "; live $LivePort after: $liveText")
        Say '--- tray log ---'
        Read-TrayLog | ForEach-Object { Say $_ }
        Say '--- end ---'
        if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
    } else {
        Say 'nothing was started or changed'
    }
}
if ($script:Failed) { Say 'SMOKE FAILED'; exit 1 }
Say 'SMOKE PASSED'
exit 0
