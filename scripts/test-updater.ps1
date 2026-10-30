param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$publishedVersion = (& $Executable --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Published executable did not start' }
$root = Join-Path (Get-Location) ('.context/updater-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
function Hash([string]$path) { return (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }
function Case([string]$name, [bool]$badHealth, [bool]$recover, [bool]$pendingGame) {
    $folder = Join-Path $root $name
    $testDataHome = Join-Path $folder 'data'
    $updates = Join-Path $testDataHome 'updates'
    New-Item -ItemType Directory -Path $updates -Force | Out-Null
    $dest = Join-Path $folder 'optibackup.exe'
    $download = Join-Path $updates 'download.exe'
    Copy-Item $Executable $dest
    Copy-Item $Executable ($dest + '.previous')
    if ($badHealth) { [IO.File]::WriteAllText($download, 'Intentionally invalid executable for rollback testing') }
    else { Copy-Item $Executable $download }
    @{ SchemaVersion = 1; Settings = @{ CheckUpdates = $false }; Games = @() } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $testDataHome 'catalog.json')
    if ($pendingGame) { '{}' | Set-Content (Join-Path $testDataHome 'pending.json') }
    $job = @{
        SchemaVersion = 1; Home = $testDataHome; Destination = $dest; Download = $download
        ExpectedHash = (Hash $download); PreviousHash = (Hash $dest); Version = ('v' + $publishedVersion)
        Status = 'staged'; ParentId = 2147483646; Restart = $false
    }
    if ($recover) { $job.Status = 'swapping'; [IO.File]::WriteAllText($dest, 'Interrupted replacement') }
    $jobPath = Join-Path $updates 'pending.json'
    $job | ConvertTo-Json -Depth 10 | Set-Content $jobPath
    $helper = Join-Path $updates 'helper.exe'
    Copy-Item $Executable $helper
    $arguments = @('--update-helper', ('"' + $jobPath + '"'))
    if ($recover) { $arguments += '--rollback-update' }
    $process = Start-Process -FilePath $helper -ArgumentList $arguments -PassThru -NoNewWindow
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Updater timed out in $name" }
    $result = Get-Content $jobPath -Raw | ConvertFrom-Json
    $expectedStatus = if ($badHealth -or $recover -or $pendingGame) { 'rolled-back' } else { 'complete' }
    if ($result.Status -ne $expectedStatus) { throw "$name status $($result.Status); expected $expectedStatus" }
    if ((Hash $dest) -ne (Hash $Executable)) { throw "$name did not preserve/restore a working executable" }
    if (-not ($badHealth -or $pendingGame) -and $process.ExitCode -ne 0) { throw "$name exit code $($process.ExitCode)" }
    if ($badHealth -and (Test-Path ($jobPath + '.healthy'))) { throw 'Broken executable unexpectedly passed health check' }
    if (-not ($badHealth -or $recover -or $pendingGame) -and -not (Test-Path ($jobPath + '.healthy'))) { throw 'Health probe did not run' }
    Write-Host "PASS updater $name"
}
try {
    Case 'success' $false $false $false
    Case 'failed-health' $true $false $false
    Case 'interrupted-replacement' $false $true $false
    Case 'pending-game-operation' $false $false $true
} finally { Remove-Item $root -Recurse -Force }
