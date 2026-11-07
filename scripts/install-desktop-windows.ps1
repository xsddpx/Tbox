param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string[]]$DependencyPath = @(),
    [switch]$VerifyCapture,
    [switch]$VerifyRecording,
    [string]$ResultDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Install on Windows.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'Missing package manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
if ($manifest.Package.Identity.Name -ne 'Tbox' -or $manifest.Package.Identity.Publisher -ne 'CN=Tbox Development' -or
    $manifest.Package.Identity.ProcessorArchitecture -ne 'x64' -or $manifest.Package.Applications.Application.Id -ne 'App' -or
    $manifest.Package.Applications.Application.Executable -ne 'Tbox.Desktop.exe') {
    throw 'This is not the shared Tbox Windows package.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne 'CN=Tbox Development') {
    throw 'The package signature must be trusted and match Tbox. This script does not import certificates or change trust.'
}
$installedBefore = @(Get-AppxPackage -Name Tbox)
if ($installedBefore.Count -gt 1) { throw 'Multiple Tbox packages are registered; resolve the installed package before upgrading.' }
if ($installedBefore.Count -eq 1 -and $installedBefore[0].Publisher -ne $signature.SignerCertificate.Subject) {
    throw 'The installed application belongs to a different publisher.'
}
if (-not $ResultDirectory) { $ResultDirectory = Join-Path $env:LOCALAPPDATA ('Tbox\install-verification\' + [Guid]::NewGuid().ToString('N')) }
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
if (Test-Path -LiteralPath $ResultDirectory) { throw 'Use a new result directory, so a stale result cannot pass verification.' }
$parameters = @{ Path = $package; ForceApplicationShutdown = $true; ErrorAction = 'Stop' }
if ($DependencyPath.Count) { $parameters.DependencyPath = $DependencyPath }
Add-AppxPackage @parameters
$installed = Get-AppxPackage -Name Tbox | Where-Object Publisher -eq 'CN=Tbox Development'
if (-not $installed) { throw 'Tbox was not registered after installation.' }
if ($installed.Version -ne [Version]$manifest.Package.Identity.Version) { throw 'Installed version differs from the requested package.' }
$executable = Join-Path $installed.InstallLocation 'Tbox.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'The installed package does not contain the shared desktop application.' }
New-Item -ItemType Directory -Path $ResultDirectory | Out-Null
$smokeArguments = "--smoke-test `"$ResultDirectory`""
if ($VerifyCapture) { $smokeArguments += ' --verify-capture' }
if ($VerifyRecording) { $smokeArguments += ' --verify-recording' }
Invoke-CommandInDesktopPackage -PackageFamilyName $installed.PackageFamilyName -AppId App -Command $executable `
    -Args $smokeArguments -PreventBreakaway
$resultPath = Join-Path $ResultDirectory 'result.json'
$deadline = [DateTime]::UtcNow.AddSeconds(150)
while (-not (Test-Path -LiteralPath $resultPath)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw "Installed application did not complete verification: $ResultDirectory" }
    Start-Sleep -Milliseconds 250
}
$result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $result.success) { throw "Installed application verification failed: $($result.error)" }
if (-not $result.nativeOcr -or -not $result.pixelCrop -or -not $result.uiRendered) { throw 'Installed verification is incomplete.' }
if ($VerifyCapture -and (-not $result.captureVerified -or $result.displayCount -lt 1)) { throw 'Actual display capture was requested but did not pass.' }
if ($VerifyRecording -and (-not $result.recording -or -not $result.recording.verified)) { throw 'Actual native recording was requested but did not pass.' }
Write-Host "Installed $($installed.Version), verified package identity, OCR, pixel crop and shared window rendering."
