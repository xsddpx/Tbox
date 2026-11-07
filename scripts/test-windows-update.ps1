param(
    [Parameter(Mandatory)][string]$BaselinePackage,
    [string]$BaselineVersion = '1.1.13',
    [string]$BaselineSha256 = 'CF793D4A313A03C3D79910256BE7384A13E6C51EB62A34D011D9F531DF1C41BF',
    [string]$TargetVersion = '1.1.15',
    [string]$OutputDirectory = 'TEMP/update-smoke'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:OS -ne 'Windows_NT') {
    throw 'Run only on a disposable GitHub Windows runner.'
}
if ($BaselineVersion -notmatch '^\d+\.\d+\.\d+$' -or $TargetVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [Version]$BaselineVersion -ge [Version]$TargetVersion -or $BaselineSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Specify an older baseline, newer target and pinned baseline SHA-256.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh evidence directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$baseline = (Resolve-Path -LiteralPath $BaselinePackage).Path
if ((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -ne $BaselineSha256) { throw 'Baseline checksum differs.' }
if (Get-AppxPackage -Name Tbox) { throw 'Expected a clean runner without Tbox.' }

# Only the public release certificate is trusted, only in this disposable user
# test environment. No private signing key or repository secret is needed.
$certificatePath = Join-Path $output 'Tbox-Development.cer'
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/xsddpx/Tbox-releases/releases/download/v$TargetVersion/Tbox-Development.cer" -OutFile $certificatePath
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.Thumbprint -ne '10859D3FDA689CC8CCC92143C79DA6CCFCDA5D92') { throw 'Unexpected release signing identity.' }
Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/xsddpx/Tbox-releases/releases/tags/v$TargetVersion"
$asset = @($release.assets | Where-Object name -eq "Tbox_$($TargetVersion).0_win-x64.msix")
if ($release.draft -or $release.prerelease -or $asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$') { throw 'Target public release is incomplete.' }
$installer = @(Get-AppxPackage Microsoft.DesktopAppInstaller)
@{ os = [Environment]::OSVersion.VersionString; appInstaller = @($installer | Select-Object Name, Version, InstallLocation) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'environment.json') -Encoding UTF8
if ($installer.Count -ne 1) { throw 'Windows App Installer is not registered on this runner; no shell replacement is used.' }

$project = 'tests/Tbox.UpdateSmoke/Tbox.UpdateSmoke.csproj'
dotnet build $project -c Release --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { throw 'Update UI fixture did not build.' }
& "$PSScriptRoot/install-desktop-windows.ps1" -PackagePath $baseline -ResultDirectory (Join-Path $output 'baseline-native')
$installed = Get-AppxPackage -Name Tbox
if ($installed.Version -ne [Version]"$BaselineVersion.0") { throw 'Installed baseline version differs.' }
$family = $installed.PackageFamilyName
function Start-Installed([string]$Arguments) {
    $package = Get-AppxPackage -Name Tbox
    Invoke-CommandInDesktopPackage -PackageFamilyName $package.PackageFamilyName -AppId App `
        -Command (Join-Path $package.InstallLocation 'Tbox.Desktop.exe') -Args $Arguments -PreventBreakaway
}
function Stop-Installed {
    Start-Installed '--quit'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (Get-Process -Name Tbox.Desktop -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Tbox did not quit.' }
        Start-Sleep -Milliseconds 100
    }
}
function Run-Fixture([string]$Phase) {
    dotnet run --project $project -c Release --no-build --no-restore -- $Phase $output $BaselineVersion $TargetVersion $family
    if ($LASTEXITCODE -ne 0) { throw "Actual installed UI verification failed in $Phase; see evidence." }
}
Start-Installed '--show'
Run-Fixture 'download'
$download = Get-Content -LiteralPath (Join-Path $output 'download-result.json') -Raw | ConvertFrom-Json
if ($download.downloadSha256 -ne $asset[0].digest.Substring(7) -or $download.downloadBytes -ne $asset[0].size) { throw 'The client download differs from the public asset.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download.downloadPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The downloaded package signature is invalid.' }
# The Windows flow explicitly asks the user to reopen Tbox after upgrading.
# Close it after its own successful handoff, then confirm the real system UI.
Stop-Installed
Run-Fixture 'install'
$deadline = [DateTime]::UtcNow.AddSeconds(120)
do {
    $updated = Get-AppxPackage -Name Tbox
    if ($updated.Version -eq [Version]"$TargetVersion.0") { break }
    if ([DateTime]::UtcNow -gt $deadline) { throw 'System installer did not register the target version.' }
    Start-Sleep -Milliseconds 250
} while ($true)
if ($updated.PackageFamilyName -ne $family) { throw 'Package identity changed during update.' }
Start-Installed '--show'
Run-Fixture 'relaunch'
Stop-Installed
$nativeOutput = Join-Path $output 'updated-native'
Start-Installed "--smoke-test `"$nativeOutput`" --verify-capture"
$nativeResultPath = Join-Path $nativeOutput 'result.json'
$deadline = [DateTime]::UtcNow.AddSeconds(150)
while (-not (Test-Path -LiteralPath $nativeResultPath)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Updated installed app did not complete native verification.' }
    Start-Sleep -Milliseconds 250
}
$native = Get-Content -LiteralPath $nativeResultPath -Raw | ConvertFrom-Json
if (-not $native.success -or -not $native.nativeOcr -or -not $native.uiRendered -or -not $native.captureVerified) { throw 'Updated installed native checks failed.' }
@{ success = $true; baselineVersion = $BaselineVersion; targetVersion = $TargetVersion;
    packageFamily = $family; publicDownloadSha256 = $download.downloadSha256;
    signatureVerified = $true; actualSystemInstaller = $true; customSettingRetained = $true;
    nativeOcr = $native.nativeOcr; captureVerified = $native.captureVerified;
    limitations = @('Disposable Windows runner, not the original user device.', 'Tamper test proves digest rejection; untrusted signer rejection is separate.') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
