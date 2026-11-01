param([Parameter(Mandatory=$true)][string]$Cli, [Parameter(Mandatory=$true)][string]$Desktop)
$ErrorActionPreference = 'Stop'
# Check the actual PE group/image resources; shell extraction may resample its chosen frame.
python3 (Join-Path $PSScriptRoot 'test-app-icons.py') $Cli $Desktop
if ($LASTEXITCODE -ne 0) { throw 'Published executable icon resources differ from the shared artwork' }
