param([Parameter(Mandatory)][string]$CertificateThumbprint)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run the remote service fixture on a disposable GitHub-hosted runner.'
}

$name = 'Tbox.RemoteServiceFixture'
$serviceName = 'TboxRemoteServiceFixture'
if ((Get-AppxPackage -Name $name) -or (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    throw 'The remote service fixture requires a clean Windows runner.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root 'TEMP\remote-service-fixture'
$layout = Join-Path $output 'layout'
New-Item -ItemType Directory -Path (Join-Path $layout 'Assets') -Force | Out-Null
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { (Test-Path "$_\makeappx.exe") -and (Test-Path "$_\signtool.exe") } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK MakeAppx and SignTool are required.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is required.' }
$serviceExe = Join-Path $layout 'RemoteServiceFixture.exe'
& $compiler /nologo /target:exe /platform:x64 /r:System.ServiceProcess.dll "/out:$serviceExe" (Join-Path $PSScriptRoot 'remote-service-fixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the test service.' }
foreach ($asset in 'StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png') {
    Copy-Item -LiteralPath (Join-Path $root "src\Tbox.Desktop\Assets\$asset") -Destination (Join-Path $layout 'Assets')
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'remote-service-fixture.appxmanifest') -Raw
$results = @()
try {
    foreach ($version in '1.0.0.0', '1.0.1.0') {
        $manifest.Package.Identity.Version = $version
        $manifest.Save((Join-Path $layout 'AppxManifest.xml'))
        $package = Join-Path $output "Tbox.RemoteServiceFixture_${version}_win-x64.msix"
        if (Test-Path -LiteralPath $package) { throw "Fixture version $version is already packaged." }
        & "$sdk\makeappx.exe" pack /d $layout /p $package /h SHA256
        if ($LASTEXITCODE -ne 0) { throw "Fixture packaging failed for $version." }
        & "$sdk\signtool.exe" sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $package
        if ($LASTEXITCODE -ne 0) { throw "Fixture signing failed for $version." }
        $signature = Get-AuthenticodeSignature -LiteralPath $package
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Fixture signature verification failed for $version."
        }
        if ($results.Count) {
            $previous = Get-AppxPackage -Name $name
            if ($previous.Version -ne [Version]$results[-1].Version) { throw 'Missing baseline package for upgrade.' }
            Stop-Service -Name $serviceName
        }
        Add-AppxPackage -Path $package -ForceApplicationShutdown
        $installed = Get-AppxPackage -Name $name
        if (-not $installed -or $installed.Version -ne [Version]$version) {
            throw "Fixture package $version was not registered."
        }
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        $expectedPath = Join-Path $installed.InstallLocation 'RemoteServiceFixture.exe'
        if (-not $service -or $service.StartMode -ne 'Auto' -or $service.StartName -ne 'LocalSystem' -or
            -not $service.PathName.StartsWith($expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
            -not $service.PathName.StartsWith(('"' + $expectedPath + '"'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixture service registration is invalid for $version."
        }
        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(25))
        $results += [pscustomobject]@{
            Version = $version
            PackageFamilyName = $installed.PackageFamilyName
            StartMode = $service.StartMode
            StartAccount = $service.StartName
            State = (Get-Service -Name $serviceName).Status.ToString()
            Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    if ($results[0].PackageFamilyName -ne $results[1].PackageFamilyName) { throw 'Fixture package family changed.' }
    ConvertTo-Json -InputObject @($results) | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
} finally {
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
    }
    Get-AppxPackage -Name $name | Remove-AppxPackage
}
