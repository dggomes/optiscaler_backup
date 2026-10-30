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
function Invoke-Desktop([string]$DataHome, [string]$ImagePath) {
    $process = Start-Process -FilePath $Executable -ArgumentList @('--home',('"'+${DataHome}+'"'),'--smoke-test',('"'+${ImagePath}+'"')) -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop smoke test timed out' }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        if (Test-Path (${ImagePath}+'.error')) { Get-Content (${ImagePath}+'.error') | Write-Host }
        throw "Desktop smoke exited with $($process.ExitCode)"
    }
}

try {
    'quality=high' | Set-Content (Join-Path $testGame 'OptiScaler.ini')
    'dxgi test fixture' | Set-Content (Join-Path $testGame 'dxgi.dll')
    $game = (Invoke-Cli -CommandArgs @('games','add','Desktop integration fixture',$testGame)) | ConvertFrom-Json
    # Optional real image fixture; provider outages must not block local UI tests.
    $coverFile = Join-Path $testRoot 'steam-cover.jpg'
    try {
        Invoke-WebRequest -Uri 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1091500/library_600x900.jpg' -OutFile $coverFile -TimeoutSec 15
        $cache = Join-Path $testData 'thumbnails'
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $identity = $game.Id + "`n" + $game.Name + "`n" + $game.Directory
        $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
        Copy-Item $coverFile (Join-Path $cache ($key + '.jpg'))
        @{ Version = 1; Checked = ([DateTimeOffset]::UtcNow).ToString('o'); AppId = 1091500; PortraitChecked = $true; Hash = (Get-FileHash $coverFile -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json | Set-Content (Join-Path $cache ($key + '.json')) -Encoding utf8
    } catch { Write-Host ('Optional live cover fixture unavailable: ' + $_.Exception.Message) }
    $null = Invoke-Cli -CommandArgs @('files','add',$game.Id,'OptiScaler.ini')
    foreach ($index in 1..53) {
        $relative = 'settings/profiles/graphics-' + $index.ToString('00') + '.ini'
        $path = Join-Path $testGame $relative
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        'quality=high' | Set-Content $path
        $null = Invoke-Cli -CommandArgs @('files','add',$game.Id,$relative)
    }
    $null = Invoke-Cli -CommandArgs @('snapshots','create',$game.Id,'Graphics baseline')
    Compress-Archive -Path (Join-Path $testGame 'OptiScaler.ini') -DestinationPath $testZip
    $map = Join-Path $testRoot 'mapping.json'
    @{ 'OptiScaler.ini' = 'OptiScaler.ini' } | ConvertTo-Json | Set-Content $map
    $null = Invoke-Cli -CommandArgs @('packages','import',$game.Id,$testZip,'Graphics settings','1','--map',$map,'--yes')
    $package = ((Invoke-Cli -CommandArgs @('packages','list',$game.Id)) | ConvertFrom-Json).Packages[0]
    $null = Invoke-Cli -CommandArgs @('packages','install',$game.Id,$package.Id,'--yes')
    'quality=custom' | Set-Content (Join-Path $testGame 'OptiScaler.ini')
    foreach ($index in 1..75) {
        $folder = Join-Path $testRoot ('library/game-' + $index)
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $null = Invoke-Cli -CommandArgs @('games','add',('Library test ' + $index.ToString('00')),$folder)
    }
    $offline = Join-Path $testRoot 'offline'
    New-Item -ItemType Directory -Path $offline -Force | Out-Null
    $null = Invoke-Cli -CommandArgs @('games','add','Unavailable drive test',$offline)
    Move-Item $offline ($offline + '-disconnected')
    $image = Join-Path $images 'desktop.png'
    Invoke-Desktop -DataHome $testData -ImagePath $image
    $freshImage = Join-Path $images "fresh.png"
    Invoke-Desktop -DataHome (Join-Path $testRoot "empty-data") -ImagePath $freshImage
    foreach ($path in @($freshImage,$image,($image+'.empty.png'),($image+'.game.png'),($image+'.managed.png'),($image+'.files-compact.png'),($image+'.packages.png'),($image+'.compact.png'),($image+'.settings.png'),($image+'.settings-bottom.png'),($image+'.update.png'),($image+'.offline.png'),($image+'.location.png'),($image+'.files.png'),($image+'.version.png'),($image+'.changes.png'),($image+'.package.png'))) {
        if (-not (Test-Path $path) -or (Get-Item $path).Length -lt 1000) { throw "Desktop render missing/empty: $path" }
    }
    Write-Host 'PASS desktop startup, pagination, global search, navigation, selection retention, 54-file compact workspace/filter, offline actions, cached thumbnail display/preferences, update controls/busy guard, recovery filtering and dialog rendering'
} finally { Remove-Item $testRoot -Recurse -Force }
