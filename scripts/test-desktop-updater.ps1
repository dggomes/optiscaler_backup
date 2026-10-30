param([Parameter(Mandatory=$true)][string]$Cli, [Parameter(Mandatory=$true)][string]$Desktop, [Parameter(Mandatory=$true)][string]$PreviousCli, [Parameter(Mandatory=$true)][string]$PreviousDesktop)
$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path $Cli).Path
$Desktop = (Resolve-Path $Desktop).Path
$PreviousCli = (Resolve-Path $PreviousCli).Path
$PreviousDesktop = (Resolve-Path $PreviousDesktop).Path
$version = (& $Cli --version).Trim()
$root = Join-Path (Get-Location) ('.context/desktop-updater-' + [guid]::NewGuid().ToString('N'))
function Hash([string]$path) { return (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Run-Helper([string]$helper, [string]$jobPath, [bool]$rollback) {
    $arguments = @('--update-helper',('"'+$jobPath+'"'))
    if ($rollback) { $arguments += '--rollback-update' }
    $process = Start-Process $helper -ArgumentList $arguments -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop updater timed out' }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -and (Test-Path ($jobPath+'.error'))) { Get-Content ($jobPath+'.error') | Write-Host }
    return $process.ExitCode
}
function Stop-Ui($process) {
    if ($null -eq $process) { return }
    foreach ($attempt in 1..40) {
        $process.Refresh()
        if ($process.HasExited) { return }
        $null = $process.CloseMainWindow()
        if ($process.WaitForExit(250)) { return }
    }
    $process.Kill(); $process.WaitForExit(); throw 'Fixture UI did not close normally'
}
function Case([string]$name, [string]$mode) {
    $folder = Join-Path $root $name
    $dataHome = Join-Path $folder 'custom data home'
    $updates = Join-Path $dataHome 'updates'
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    $catalog = Join-Path $dataHome 'catalog.json'
    @{ SchemaVersion = 2; Settings = @{ CheckUpdates = $false; DownloadThumbnails = $false }; Games = @() } | ConvertTo-Json -Depth 10 | Set-Content $catalog
    $catalogHash = Hash $catalog
    $ui = Join-Path $folder 'optibackup-ui.exe'
    $cliDest = Join-Path $folder 'optibackup.exe'
    Copy-Item $PreviousDesktop $ui
    $uiDownload = Join-Path $updates 'ui.exe'; Copy-Item $Desktop $uiDownload
    $companions = @()
    if ($mode -ne 'ui-only') {
        Copy-Item $PreviousCli $cliDest
        $cliDownload = Join-Path $updates 'cli.exe'
        if ($mode -eq 'invalid-pe') { [IO.File]::WriteAllText($cliDownload,'Invalid companion executable fixture') }
        elseif ($mode -eq 'bad-health') { Copy-Item $Desktop $cliDownload }
        else { Copy-Item $Cli $cliDownload }
        $companions += @{ Destination = $cliDest; Download = $cliDownload; ExpectedHash = (Hash $cliDownload); PreviousHash = (Hash $cliDest) }
    }
    $job = @{
        SchemaVersion = $(if ($companions.Count -gt 0) { 2 } else { 1 }); Home = $dataHome; Destination = $ui; Download = $uiDownload
        ExpectedHash = (Hash $uiDownload); PreviousHash = (Hash $ui); Version = ('v'+$version)
        Status = 'staged'; ParentId = 2147483646; Restart = ($mode -eq 'restart'); Companions = $companions
    }
    if ($mode -eq 'interrupted') {
        Copy-Item $ui ($ui+'.previous'); Copy-Item $cliDest ($cliDest+'.previous')
        Copy-Item $Desktop $ui -Force; $job.Status = 'swapping'
    }
    if ($mode -eq 'pending-game') { '{}' | Set-Content (Join-Path $dataHome 'pending.json') }
    $jobPath = Join-Path $updates 'pending.json'; $job | ConvertTo-Json -Depth 10 | Set-Content $jobPath
    $helper = Join-Path $updates 'helper.exe'; Copy-Item $Desktop $helper
    $held = $null; $running = $null; $restarted = $null
    try {
        if ($mode -eq 'locked') { $held = [IO.File]::Open($cliDest,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None) }
        if ($mode -eq 'running') {
            $otherHome = Join-Path $folder 'other data'
            New-Item -ItemType Directory -Path $otherHome -Force | Out-Null
            Copy-Item $catalog (Join-Path $otherHome 'catalog.json')
            $running = Start-Process $ui -ArgumentList @('--home',('"'+$otherHome+'"')) -PassThru
            $null = $running.Handle
            Start-Sleep -Milliseconds 1200
            if ($running.HasExited) { throw 'Running-companion fixture exited too early' }
        }
        $exitCode = Run-Helper $helper $jobPath ($mode -eq 'interrupted')
        if ($held) { $held.Dispose(); $held = $null }
        if ($running) { Stop-Ui $running; $running = $null }
        if ($mode -in @('locked','running')) {
            if ($exitCode -eq 0 -or (Hash $ui) -ne (Hash $PreviousDesktop) -or (Hash $cliDest) -ne (Hash $PreviousCli)) { throw "$name changed a locked/running installation" }
            $null = Run-Helper $helper $jobPath $true
        }
        $rollback = $mode -in @('invalid-pe','bad-health','interrupted','pending-game','locked','running')
        if ($mode -eq 'restart') {
            if ($exitCode -ne 0 -or (Test-Path $jobPath)) { throw 'Restart did not archive the completed update' }
            $result = Get-Content (Join-Path $updates 'last-update.json') -Raw | ConvertFrom-Json
            foreach ($attempt in 1..60) {
                $restarted = Get-Process 'optibackup-ui' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $ui } | Select-Object -First 1
                if ($restarted) {
                    try { $probe = [IO.File]::Open((Join-Path $dataHome 'session.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose() }
                    catch [IO.IOException] { break }
                }
                Start-Sleep -Milliseconds 250
            }
            if (-not $restarted) { throw 'Updated UI did not restart' }
            try { $probe = [IO.File]::Open((Join-Path $dataHome 'session.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose(); throw 'Restart lost the selected data home' }
            catch [IO.IOException] { }
            Stop-Ui $restarted; $restarted = $null
        } else { $result = Get-Content $jobPath -Raw | ConvertFrom-Json }
        $expectedStatus = if ($rollback) { 'rolled-back' } else { 'complete' }
        if ($result.Status -ne $expectedStatus) { throw "$name status $($result.Status), expected $expectedStatus" }
        $expectedUi = if ($rollback) { $PreviousDesktop } else { $Desktop }
        if ((Hash $ui) -ne (Hash $expectedUi)) { throw "$name did not restore/update the UI" }
        if ($companions.Count -gt 0) {
            $expectedCli = if ($rollback) { $PreviousCli } else { $Cli }
            if ((Hash $cliDest) -ne (Hash $expectedCli)) { throw "$name did not restore/update the CLI" }
        }
        if ($mode -ne 'restart' -and (Hash $catalog) -ne $catalogHash) { throw "$name modified user data during health checks" }
        if (-not $rollback -and -not (Test-Path ($jobPath+'.healthy'))) { throw 'UI health probe did not run' }
        if (-not $rollback -and $companions.Count -gt 0 -and -not (Test-Path ($jobPath+'.healthy-optibackup.exe'))) { throw 'CLI health probe did not run' }
        Write-Host "PASS desktop updater $name"
    } finally {
        if ($held) { $held.Dispose() }
        if ($running) { Stop-Ui $running }
        if ($restarted) { Stop-Ui $restarted }
    }
}
try {
    foreach ($path in @($PreviousCli,$PreviousDesktop)) {
        $expected = ((Get-Content ($path+'.sha256') -Raw).Trim() -split '\s+')[0]
        if ((Hash $path) -ne $expected) { throw 'Previous paired release checksum mismatch' }
    }
    Case 'ui-only-success' 'ui-only'
    Case 'paired-success' 'success'
    Case 'paired-invalid-executable' 'invalid-pe'
    Case 'paired-failed-health' 'bad-health'
    Case 'paired-interrupted' 'interrupted'
    Case 'paired-pending-game' 'pending-game'
    Case 'paired-locked-companion' 'locked'
    Case 'paired-running-companion' 'running'
    Case 'restart-preserves-data-home' 'restart'
} finally { if (Test-Path $root) { Remove-Item $root -Recurse -Force } }
