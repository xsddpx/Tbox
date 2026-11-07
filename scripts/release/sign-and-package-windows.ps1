param([switch]$VerifyIdentityOnly, [switch]$VerifyCapture)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This wrapper only runs on disposable GitHub-hosted runners.'
}
foreach ($name in 'TBOX_WINDOWS_PFX_BASE64', 'TBOX_WINDOWS_PFX_PASSWORD', 'TBOX_WINDOWS_CERT_SHA1') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Missing release configuration: $name" }
}
if ($env:TBOX_WINDOWS_CERT_SHA1 -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid certificate fingerprint.' }
$pfx = Join-Path $env:RUNNER_TEMP 'tbox-signing.pfx'
$certificates = @()
$trusted = @()
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:TBOX_WINDOWS_PFX_BASE64))
    $password = ConvertTo-SecureString $env:TBOX_WINDOWS_PFX_PASSWORD -AsPlainText -Force
    $certificates = @(Import-PfxCertificate -FilePath $pfx -Password $password -CertStoreLocation Cert:\CurrentUser\My)
    Remove-Item -LiteralPath $pfx -Force
    Remove-Item Env:\TBOX_WINDOWS_PFX_BASE64, Env:\TBOX_WINDOWS_PFX_PASSWORD
    $signer = @($certificates | Where-Object { $_.HasPrivateKey })
    if ($signer.Count -ne 1 -or $signer[0].Thumbprint -ne $env:TBOX_WINDOWS_CERT_SHA1 -or $signer[0].Subject -ne 'CN=Tbox Development') {
        throw 'PFX must contain exactly the configured persistent Tbox signing identity.'
    }
    $public = Join-Path $env:RUNNER_TEMP 'tbox-public.cer'
    Export-Certificate -Cert $signer[0] -FilePath $public | Out-Null
    # Trust only this leaf certificate on the disposable runner; never install a root CA.
    $trusted = @(Import-Certificate -FilePath $public -CertStoreLocation Cert:\LocalMachine\TrustedPeople)
    if ($VerifyIdentityOnly) {
        $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
            Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $signTool) { throw 'Windows SDK SignTool is required.' }
        # MSIX uses package trust policy; a PE file would require different root
        # trust and would not validate the TrustedPeople installation contract.
        $makeAppx = Join-Path (Split-Path $signTool -Parent) 'makeappx.exe'
        $layout = Join-Path $env:RUNNER_TEMP 'tbox-signing-fixture'
        New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\where.exe') -Destination (Join-Path $layout 'Tbox.Desktop.exe')
        foreach ($asset in 'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png', 'Wide310x150Logo.png') {
            Copy-Item -LiteralPath (Join-Path $root "src\Tbox.App\Assets\$asset") -Destination (Join-Path $layout 'Assets')
        }
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'src\Tbox.Desktop\Platform\windows\Package.appxmanifest') -Raw
        $manifest.Package.Identity.Name = 'Tbox.SigningFixture'
        $manifest.Package.Identity.Version = '1.0.0.0'
        $manifest.Save((Join-Path $layout 'AppxManifest.xml'))
        $fixture = Join-Path $env:RUNNER_TEMP 'tbox-signing-fixture.msix'
        & $makeAppx pack /d $layout /p $fixture /h SHA256
        if ($LASTEXITCODE -ne 0) { throw 'Fixture packaging failed.' }
        & $signTool sign /sha1 $signer[0].Thumbprint /fd SHA256 $fixture
        if ($LASTEXITCODE -ne 0) { throw 'Fixture signing failed.' }
        & $signTool verify /pa /v $fixture
        if ($LASTEXITCODE -ne 0) { throw 'Fixture signature validation failed.' }
        $actual = Get-AuthenticodeSignature -LiteralPath $fixture
        if ($actual.SignerCertificate.Thumbprint -ne $env:TBOX_WINDOWS_CERT_SHA1) { throw 'Fixture signer mismatch.' }
        return
    }
    if (-not (Get-AppxPackage -Name Microsoft.VCLibs.140.00.UWPDesktop | Where-Object { $_.Architecture -eq 'X64' -and $_.Version -ge [Version]'14.0.30704.0' })) {
        $dependency = Join-Path $env:RUNNER_TEMP 'Microsoft.VCLibs.x64.14.00.Desktop.appx'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile $dependency
        $signature = Get-AuthenticodeSignature -LiteralPath $dependency
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
            throw 'VCLibs dependency must have a valid Microsoft signature.'
        }
        Add-AppxPackage -Path $dependency
    }
    & "$PSScriptRoot/../package-desktop-windows.ps1" -CertificateThumbprint $signer[0].Thumbprint -TimestampUrl 'http://timestamp.digicert.com' -VerifyCapture:$VerifyCapture
} finally {
    Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue
    foreach ($certificate in $trusted) { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue }
    foreach ($certificate in $certificates) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue }
}
