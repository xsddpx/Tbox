param(
    [switch]$VerifyOnly,
    [switch]$VerifyCapture,
    [switch]$VerifyRecording,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on Windows with the Windows SDK installed.' }
. (Join-Path $PSScriptRoot 'windows-package-policy.ps1')
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Tbox.Desktop\Tbox.Desktop.csproj'
$manifest = Join-Path $root 'src\Tbox.Desktop\Platform\windows\Package.appxmanifest'
$history = Join-Path $root 'docs\release-history.md'
[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
$version = [string]$manifestXml.Package.Identity.Version
$package = Join-Path $root "artifacts\Tbox_${version}_win-x64.msix"
$mutex = [Threading.Mutex]::new($false, 'Local\Tbox.SharedDesktopPackaging')
$held = $false
function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Another shared desktop package build is running.' }
    $version = Assert-DesktopPackageVersion $project $manifest $history $package -VerificationOnly:$VerifyOnly
    $historyBefore = Get-Content -LiteralPath $history -Raw -Encoding UTF8
    $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
        Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64' } |
        Where-Object { (Test-Path (Join-Path $_ 'makeappx.exe')) -and (Test-Path (Join-Path $_ 'signtool.exe')) } | Select-Object -First 1
    if (-not $sdk) { throw 'Windows SDK MakeAppx and SignTool are required.' }
    $makeAppx = Join-Path $sdk 'makeappx.exe'; $signTool = Join-Path $sdk 'signtool.exe'
    if (-not $VerifyOnly) {
        if (-not $CertificateThumbprint) { throw 'Pass the existing signing certificate thumbprint; this script never generates a certificate.' }
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
        if ($certificate.Subject -ne [string]$manifestXml.Package.Identity.Publisher -or -not $certificate.HasPrivateKey -or
            $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or
            @($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
            throw 'The existing certificate must be valid for code signing and match the package Publisher.'
        }
    }
    $work = Join-Path $root ('TEMP\windows-package\' + [Guid]::NewGuid().ToString('N'))
    $layout = Join-Path $work 'layout'; $unpacked = Join-Path $work 'unpacked'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Push-Location $root
    try {
        Invoke-Checked python @('scripts/verify-windows-media.py')
        Invoke-Checked dotnet @('test', 'tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj', '-c', 'Release')
        Invoke-Checked dotnet @('test', 'tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj', '-c', 'Release')
        Invoke-Checked dotnet @('run', '--project', 'tests/Tbox.ClipboardSmoke/Tbox.ClipboardSmoke.csproj', '-c', 'Release')
        Invoke-Checked dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:TboxWindows=true', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $layout)
        Assert-DesktopPayload $layout $version
        Invoke-Checked python @('scripts/verify-windows-media.py', (Join-Path $layout 'ThirdParty\FFmpeg'))
        New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
        $assetSource = Join-Path $root 'src\Tbox.Desktop\Assets'
        foreach ($pattern in 'StoreLogo.png', 'Square44x44Logo*.png', 'Square150x150Logo.png', 'Wide310x150Logo.png') {
            Copy-Item -Path (Join-Path $assetSource $pattern) -Destination (Join-Path $layout 'Assets')
        }
        Assert-DesktopIconAssets $layout
        Copy-Item -LiteralPath $manifest -Destination (Join-Path $layout 'AppxManifest.xml')
        Invoke-Checked dotnet @('run', '--project', 'tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj', '-c', 'Release', '--',
            'assets/media-test/fixture.mp4', (Join-Path $work 'media'),
            (Join-Path $layout 'ThirdParty\FFmpeg\bin\ffmpeg.exe'), (Join-Path $layout 'libvlc\win-x64'))
    } finally { Pop-Location }
    $candidate = Join-Path $work "Tbox_${version}_win-x64.msix"
    Invoke-Checked $makeAppx @('pack', '/d', $layout, '/p', $candidate, '/h', 'SHA256')
    Invoke-Checked $makeAppx @('unpack', '/p', $candidate, '/d', $unpacked)
    Assert-DesktopPayload $unpacked $version
    Assert-DesktopIconAssets $unpacked
    if ($VerifyOnly) {
        # Deliberately not a release artifact: no signing, trust changes or installation in CI.
        $report = [ordered]@{ version = $version; kind = 'verification-unsigned'; package = $candidate;
            sha256 = (Get-FileHash $candidate -Algorithm SHA256).Hash; payloadValidated = $true; mediaValidated = $true;
            signed = $false; installed = $false; sourceCommit = (& git -C $root rev-parse HEAD) }
        $report | ConvertTo-Json | Set-Content (Join-Path $work 'verification.json') -Encoding UTF8
        Write-Host "Unsigned package verification passed: $work"
        return
    }
    $signArguments = @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256')
    if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    Invoke-Checked $signTool ($signArguments + $candidate)
    Invoke-Checked $signTool @('verify', '/pa', '/v', $candidate)
    & (Join-Path $PSScriptRoot 'install-desktop-windows.ps1') -PackagePath $candidate -ResultDirectory (Join-Path $work 'installed-smoke') -VerifyCapture:$VerifyCapture -VerifyRecording:$VerifyRecording
    if ((Get-Content -LiteralPath $history -Raw -Encoding UTF8) -ne $historyBefore) { throw 'Release history changed during validation; keep the verified candidate and retry with a new version.' }
    $hash = (Get-FileHash $candidate -Algorithm SHA256).Hash
    $stamp = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd HH:mm:ss zzz')
    $updated = Add-DesktopPackageHistory $historyBefore $version $stamp $hash
    New-Item -ItemType Directory -Path (Split-Path $package -Parent) -Force | Out-Null
    $publicCertificate = Join-Path $work 'Tbox-Development.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
    Copy-Item -LiteralPath $publicCertificate -Destination (Join-Path (Split-Path $package -Parent) 'Tbox-Development.cer') -Force
    $temporaryHistory = Join-Path $work 'release-history.new'
    [IO.File]::WriteAllText($temporaryHistory, $updated, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($candidate, $package) # No overwrite, even if another process created the version.
    try { [IO.File]::Replace($temporaryHistory, $history, (Join-Path $work 'release-history.backup'), $true) }
    catch { [IO.File]::Move($package, $candidate); throw }
    Write-Host "Installed and verified: $package`nSHA-256: $hash"
}
finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
