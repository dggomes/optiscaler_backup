param([Parameter(Mandatory=$true)][string]$Cli, [Parameter(Mandatory=$true)][string]$Desktop)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconFile = Join-Path $PSScriptRoot '../src/windows/assets/optibackup.ico'
foreach ($executable in @($Cli, $Desktop)) {
    $icon = [Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path $executable).Path)
    if ($null -eq $icon) { throw "Missing executable icon: $executable" }
    $actual = $icon.ToBitmap()
    $expectedIcon = New-Object Drawing.Icon -ArgumentList @((Resolve-Path $iconFile).Path, (New-Object Drawing.Size -ArgumentList $actual.Width,$actual.Height))
    $expected = $expectedIcon.ToBitmap()
    try {
        if ($actual.Width -ne $expected.Width -or $actual.Height -ne $expected.Height) { throw "Executable icon dimensions differ: $executable" }
        $checked = 0
        for ($y = 0; $y -lt $expected.Height; $y++) {
            for ($x = 0; $x -lt $expected.Width; $x++) {
                $pixel = $expected.GetPixel($x, $y)
                if ($pixel.A -ne 255) { continue }
                $other = $actual.GetPixel($x, $y)
                if ($pixel.R -ne $other.R -or $pixel.G -ne $other.G -or $pixel.B -ne $other.B) { throw "Executable icon pixels differ: $executable" }
                $checked++
            }
        }
        if ($checked -lt 100) { throw "Executable icon contains insufficient visible artwork: $executable" }
        Write-Host "PASS embedded application icon: $executable ($($actual.Width)x$($actual.Height))"
    } finally {
        $expected.Dispose(); $expectedIcon.Dispose(); $actual.Dispose(); $icon.Dispose()
    }
}
