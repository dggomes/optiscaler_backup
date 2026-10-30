param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$Cli)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$Cli = (Resolve-Path $Cli).Path
$testRoot = Join-Path (Get-Location) ('.context/desktop-' + [guid]::NewGuid().ToString('N'))
$testData = Join-Path $testRoot 'data'
$testGame = Join-Path $testRoot 'game'
$testZip = Join-Path $testRoot 'settings.zip'
$images = Join-Path (Get-Location) 'artifacts/desktop-screenshots'
New-Item -ItemType Directory -Path $testGame,$images -Force | Out-Null
function Invoke-Cli([string[]]$CommandArgs) {
    $output = & $Cli --home $testData @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "CLI fixture setup failed: $($CommandArgs -join ' ')" }
    return $output
}
try {
    'quality=high' | Set-Content (Join-Path $testGame 'OptiScaler.ini')
    'dxgi test fixture' | Set-Content (Join-Path $testGame 'dxgi.dll')
    $game = (Invoke-Cli -CommandArgs @('games','add','Desktop integration fixture',$testGame)) | ConvertFrom-Json
    $null = Invoke-Cli -CommandArgs @('files','add',$game.Id,'OptiScaler.ini')
    $null = Invoke-Cli -CommandArgs @('snapshots','create',$game.Id,'Original','--original','--confirm-clean')
    Compress-Archive -Path (Join-Path $testGame 'OptiScaler.ini') -DestinationPath $testZip
    $map = Join-Path $testRoot 'mapping.json'
    @{ 'OptiScaler.ini' = 'OptiScaler.ini' } | ConvertTo-Json | Set-Content $map
    $null = Invoke-Cli -CommandArgs @('packages','import',$game.Id,$testZip,'Graphics settings','1','--map',$map,'--yes')
    $image = Join-Path $images 'desktop.png'
    $process = Start-Process -FilePath $Executable -ArgumentList @('--home',('"'+$testData+'"'),'--smoke-test',('"'+$image+'"')) -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop smoke test timed out' }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        if (Test-Path ($image+'.error')) { Get-Content ($image+'.error') | Write-Host }
        throw "Desktop smoke exited with $($process.ExitCode)"
    }
    foreach ($path in @($image,($image+'.empty.png'),($image+'.files.png'),($image+'.version.png'),($image+'.changes.png'),($image+'.package.png'))) {
        if (-not (Test-Path $path) -or (Get-Item $path).Length -lt 1000) { throw "Desktop render missing/empty: $path" }
    }
    Write-Host 'PASS desktop startup, search, selection and dialog rendering'
} finally { Remove-Item $testRoot -Recurse -Force }
