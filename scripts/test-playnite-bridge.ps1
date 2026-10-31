param([Parameter(Mandatory=$true)][string]$Assembly, [Parameter(Mandatory=$true)][string]$Cli)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Resolve-Path $Assembly).Path
$root = Join-Path (Get-Location) ('.context/bridge-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$source = Join-Path $root 'cover.jpg'
[IO.File]::WriteAllBytes($source, [byte[]]@(255,216,255,224,0,2,255,217))
$game = New-Object OptiScalerBackup.PlayniteBridge.ExportGame
$game.Id = [guid]::NewGuid().ToString(); $game.Name = 'SDK export integration fixture'
$game.InstallDirectory = $root; $game.Installed = $true; $game.CoverSource = $source
$game.Platforms = @('PC'); $game.Tags = @('Test tag'); $game.Categories = @('Test category')
$games = New-Object 'System.Collections.Generic.List[OptiScalerBackup.PlayniteBridge.ExportGame]'
$games.Add($game)
$export = Join-Path $root 'export'
[OptiScalerBackup.PlayniteBridge.ExportWriter]::Write($export, $games)
[OptiScalerBackup.PlayniteBridge.ExportWriter]::Write($export, $games)
$document = Get-Content (Join-Path $export 'library.json') -Raw | ConvertFrom-Json
if ($document.Version -ne 1 -or @($document.Games).Count -ne 1 -or $document.Games[0].Platforms[0] -ne 'PC') { throw 'SDK export metadata did not round trip' }
if ($document.Games[0].PSObject.Properties.Name -contains 'CoverSource') { throw 'Private cover source leaked into export' }
if (-not (Test-Path (Join-Path $export $document.Games[0].Cover))) { throw 'Cover was not exported' }
$report = & $Cli --home (Join-Path $root 'app') playnite compare (Join-Path $export 'library.json') --json
if ($LASTEXITCODE -ne 0) { throw 'App could not compare the actual compiled bridge export' }
$comparison = $report | ConvertFrom-Json
if ($comparison.Comparison.PlayniteTotal -ne 1 -or $comparison.Comparison.MissingInstalled -ne 1) { throw 'Bridge/app comparison counts disagree' }
[IO.File]::WriteAllText((Join-Path $export 'library.json'), '{"Version":99}')
$rejected = $false
try { [OptiScalerBackup.PlayniteBridge.ExportWriter]::Write($export, $games) } catch { $rejected = $true }
if (-not $rejected -or (Get-Content (Join-Path $export 'library.json') -Raw) -ne '{"Version":99}') { throw 'Future export was overwritten' }
Write-Host 'PASS compiled Playnite bridge export/replace, cover copy, metadata privacy and CLI comparison interoperability'
