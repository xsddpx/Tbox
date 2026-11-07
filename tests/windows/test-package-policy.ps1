$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root 'scripts\windows-package-policy.ps1')
$directory = Join-Path $root ('TEMP\package-policy-tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
try {
    $project = Join-Path $directory 'test.csproj'
    $manifest = Join-Path $directory 'AppxManifest.xml'
    $history = Join-Path $directory 'history.md'
    $package = Join-Path $directory 'candidate.msix'
    $originalProject = Get-Content (Join-Path $root 'src\Tbox.Desktop\Tbox.Desktop.csproj') -Raw -Encoding UTF8
    $originalManifest = Get-Content (Join-Path $root 'src\Tbox.Desktop\Platform\windows\Package.appxmanifest') -Raw -Encoding UTF8
    [xml]$original = $originalManifest
    $version = [string]$original.Package.Identity.Version
    Set-Content -Encoding UTF8 $project $originalProject; Set-Content -Encoding UTF8 $manifest $originalManifest; Set-Content -Encoding UTF8 $history '# Test history'
    function Expect-Rejection {
        param([string]$Case)
        $rejected = $false
        try { $null = Assert-DesktopPackageVersion $project $manifest $history $package } catch { $rejected = $true }
        if (-not $rejected) { throw "Policy accepted invalid input: $Case" }
    }
    if ((Assert-DesktopPackageVersion $project $manifest $history $package) -ne $version) { throw 'Valid project rejected.' }
    Set-Content -Encoding UTF8 $project ($originalProject -replace '<FileVersion>[^<]+</FileVersion>', '<FileVersion>0.0.1.0</FileVersion>')
    Expect-Rejection 'version mismatch'
    Set-Content -Encoding UTF8 $project $originalProject
    Set-Content -Encoding UTF8 $manifest ($originalManifest -replace 'Executable="Tbox.Desktop.exe"', 'Executable="Tbox.App.exe"')
    Expect-Rejection 'legacy executable'
    Set-Content -Encoding UTF8 $manifest ($originalManifest -replace 'Name="Tbox"', 'Name="Tbox.New"')
    Expect-Rejection 'changed package family'
    Set-Content -Encoding UTF8 $manifest $originalManifest
    Set-Content -Encoding UTF8 $history "| $version | 2026-01-01 | hash |"
    Expect-Rejection 'previously packaged version'
    Set-Content -Encoding UTF8 $history '# Test history'
    Set-Content $package 'existing artifact'
    Expect-Rejection 'existing artifact'
    $rejected = $false
    try { Assert-DesktopPayload $directory $version } catch { $rejected = $true }
    if (-not $rejected) { throw 'Incomplete runtime accepted.' }
    Write-Host 'Package policy: valid configuration accepted; six invalid cases rejected.'
} finally { Remove-Item -LiteralPath $directory -Recurse -Force }
