param(
    [string[]]$Languages = @("zh-CN", "en-US")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = Join-Path $root "src\Tbox.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
$layout = Join-Path $root "artifacts\msix-layout"
$manifest = Join-Path $root "src\Tbox.App\Package.appxmanifest"
$sdkTools = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64"

if (-not (Test-Path -LiteralPath (Join-Path $output "Tbox.App.exe"))) {
    throw "Release output not found. Build Tbox.App in Release x64 first."
}

if (-not $layout.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe MSIX layout path: $layout"
}

if (Test-Path -LiteralPath $layout) {
    Remove-Item -LiteralPath $layout -Recurse -Force
}

New-Item -ItemType Directory -Path $layout | Out-Null
Copy-Item -Path (Join-Path $output "*") -Destination $layout -Recurse -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $layout "AppxManifest.xml") -Force
Copy-Item -LiteralPath (Join-Path $output "Tbox.App.pri") -Destination (Join-Path $layout "resources.pri") -Force

$keptLanguages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($language in $Languages) {
    [void]$keptLanguages.Add($language)
}

$removedLanguages = @()
foreach ($directory in Get-ChildItem -LiteralPath $layout -Directory) {
    $files = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File)
    $isLanguageDirectory = $files.Count -gt 0 -and @($files | Where-Object Extension -ne ".mui").Count -eq 0
    if ($isLanguageDirectory -and -not $keptLanguages.Contains($directory.Name)) {
        $removedLanguages += $directory.Name
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}

[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
$version = $manifestXml.Package.Identity.Version
$packagePath = Join-Path $root "artifacts\Tbox_${version}_x64.msix"
$makeAppx = Join-Path $sdkTools "makeappx.exe"
$signTool = Join-Path $sdkTools "signtool.exe"

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

& $makeAppx pack /d $layout /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -eq "CN=Tbox Development" |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    throw "Tbox development signing certificate was not found."
}

& $signTool sign /sha1 $certificate.Thumbprint /fd SHA256 $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE."
}

& $signTool verify /pa $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MSIX signature verification failed with exit code $LASTEXITCODE."
}

Write-Host "Kept languages: $($Languages -join ', ')"
Write-Host "Removed language directories: $($removedLanguages.Count)"
Get-Item -LiteralPath $packagePath
