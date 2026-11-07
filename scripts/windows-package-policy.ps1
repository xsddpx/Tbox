Set-StrictMode -Version Latest

function Assert-DesktopPackageVersion {
    param([string]$Project, [string]$Manifest, [string]$History, [string]$PackagePath)
    [xml]$projectXml = Get-Content -LiteralPath $Project -Raw -Encoding UTF8
    [xml]$manifestXml = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8
    $properties = $projectXml.Project.PropertyGroup | Where-Object { $_.PSObject.Properties.Name -contains 'Version' } | Select-Object -First 1
    $version = [string]$manifestXml.Package.Identity.Version
    if ($version -notmatch '^\d+\.\d+\.\d+\.0$' -or
        "$($properties.Version).0" -ne $version -or
        [string]$properties.AssemblyVersion -ne $version -or [string]$properties.FileVersion -ne $version) {
        throw 'Project and Windows package versions must agree.'
    }
    if ($manifestXml.Package.Identity.Name -ne 'Tbox' -or $manifestXml.Package.Identity.Publisher -ne 'CN=Tbox Development' -or
        $manifestXml.Package.Identity.ProcessorArchitecture -ne 'x64' -or
        $manifestXml.Package.Applications.Application.Id -ne 'App' -or
        $manifestXml.Package.Applications.Application.Executable -ne 'Tbox.Desktop.exe') {
        throw 'Windows package identity or shared desktop entry point changed.'
    }
    if (Test-Path -LiteralPath $PackagePath) { throw "Package already exists: $PackagePath" }
    if ((Get-Content -LiteralPath $History -Raw -Encoding UTF8) -match "(?m)^\|\s*$([regex]::Escape($version))\s*\|") {
        throw "Version $version was already packaged. Increase the version."
    }
    return $version
}

function Assert-DesktopPayload {
    param([string]$Directory, [string]$Version)
    $required = @('Tbox.Desktop.exe', 'Tbox.Desktop.dll', 'Tbox.Desktop.runtimeconfig.json', 'coreclr.dll',
        'hostfxr.dll', 'ScreenRecorderLib.dll', 'ThirdParty\FFmpeg\bin\ffmpeg.exe',
        'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\libvlccore.dll',
        'ThirdParty\THIRD-PARTY-NOTICES.md', 'ThirdParty\LibVLC-LGPL-2.1.txt',
        'ThirdParty\VLC-GPL-2.txt', 'ThirdParty\LibVLCSharp-LICENSE.txt', 'ThirdParty\FFmpeg\LICENSE.txt',
        'ThirdParty\Avalonia-LICENSE.txt', 'ThirdParty\ScreenRecorderLib-LICENSE.txt')
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $relative) -PathType Leaf)) { throw "Missing packaged runtime: $relative" }
    }
    $actual = (Get-Item -LiteralPath (Join-Path $Directory 'Tbox.Desktop.exe')).VersionInfo.FileVersion
    if ($actual -ne $Version) { throw "Stale executable: $actual, expected $Version" }
    if (@(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object Extension -in '.pfx', '.p12', '.key').Count) {
        throw 'Signing credentials must never be included in a package.'
    }
}
