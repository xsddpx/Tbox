param(
    [string[]]$Languages = @("zh-CN", "en-US"),
    [ValidateRange(1, 100)]
    [int]$IntegrationAttempts = 20
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$packageMutex = [Threading.Mutex]::new($false, "Local\Tbox.PackageMsix")
$packageMutexHeld = $false
try {
    try {
        $packageMutexHeld = $packageMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $packageMutexHeld = $true
    }
    if (-not $packageMutexHeld) {
        throw "Another Tbox packaging process is already running."
    }

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = Join-Path $root "src\Tbox.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
$layout = Join-Path $root "artifacts\msix-layout"
$manifest = Join-Path $root "src\Tbox.App\Package.appxmanifest"
$project = Join-Path $root "src\Tbox.App\Tbox.App.csproj"
$history = Join-Path $root "docs\release-history.md"
$solution = Join-Path $root "Tbox.sln"
$coreTests = Join-Path $root "tests\Tbox.Core.Tests\bin\Release\net8.0\Tbox.Core.Tests.dll"
$integrationExecutable = Join-Path $root "tests\Tbox.IntegrationHarness\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\Tbox.IntegrationHarness.exe"

$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere)) {
    throw "Visual Studio Installer discovery tool was not found: $vsWhere"
}
$visualStudioPath = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $visualStudioPath) {
    throw "Visual Studio 2022 Build Tools with MSBuild were not found."
}
$msBuild64 = Join-Path $visualStudioPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
$msBuild = if (Test-Path -LiteralPath $msBuild64) {
    $msBuild64
}
else {
    Join-Path $visualStudioPath "MSBuild\Current\Bin\MSBuild.exe"
}
$vsTest = Join-Path $visualStudioPath "Common7\IDE\Extensions\TestPlatform\vstest.console.exe"
if (-not (Test-Path -LiteralPath $msBuild) -or -not (Test-Path -LiteralPath $vsTest)) {
    throw "MSBuild or VSTest was not found under: $visualStudioPath"
}

$sdkBinRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$sdkTools = Get-ChildItem -LiteralPath $sdkBinRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object { try { [Version]$_.Name } catch { [Version]"0.0" } } -Descending |
    ForEach-Object { Join-Path $_.FullName "x64" } |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ "makeappx.exe")) -and
        (Test-Path -LiteralPath (Join-Path $_ "signtool.exe"))
    } |
    Select-Object -First 1
if (-not $sdkTools) {
    throw "Windows SDK x64 MakeAppx/SignTool were not found under: $sdkBinRoot"
}
$makeAppx = Join-Path $sdkTools "makeappx.exe"
$signTool = Join-Path $sdkTools "signtool.exe"

[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = [string]$manifestXml.Package.Identity.Version
$versionPropertyGroup = $projectXml.Project.PropertyGroup | Where-Object FileVersion | Select-Object -First 1
$projectVersion = [string]$versionPropertyGroup.Version
$projectAssemblyVersion = [string]$versionPropertyGroup.AssemblyVersion
$projectFileVersion = [string]$versionPropertyGroup.FileVersion
$normalizedProjectVersion = if ($projectVersion -match '^\d+\.\d+\.\d+$') { "$projectVersion.0" } else { $projectVersion }
$declaredVersions = @($normalizedProjectVersion, $projectAssemblyVersion, $projectFileVersion)
if (@($declaredVersions | Where-Object { [Version]$_ -ne [Version]$version }).Count -gt 0) {
    throw "Version mismatch: Version=$projectVersion, AssemblyVersion=$projectAssemblyVersion, FileVersion=$projectFileVersion, package Version=$version."
}

if (-not (Test-Path -LiteralPath $history)) {
    throw "Release history not found: $history"
}
$historyContent = Get-Content -LiteralPath $history -Raw -Encoding UTF8
$escapedVersion = [Regex]::Escape($version)
if ($historyContent -match "(?m)^\|\s*$escapedVersion\s*\|") {
    throw "Version $version already exists in release history. Increase the version before packaging."
}

& $msBuild $solution /restore /t:Build /p:Configuration=Release /p:Platform=x64 /m:1 /nologo /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $coreTests)) {
    throw "Core test assembly not found after build: $coreTests"
}
& $vsTest $coreTests /Platform:x64
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $integrationExecutable)) {
    throw "Integration harness not found after build: $integrationExecutable"
}
& $integrationExecutable $IntegrationAttempts
if ($LASTEXITCODE -ne 0) {
    throw "Integration harness failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $output "Tbox.App.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Release output not found. Build Tbox.App in Release x64 first."
}

$executableVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
if ([Version]$executableVersion -ne [Version]$version) {
    throw "Release output is stale: executable FileVersion=$executableVersion, package Version=$version. Rebuild Release x64."
}
$resourceIndex = Join-Path $output "Tbox.App.pri"
if (-not (Test-Path -LiteralPath $resourceIndex)) {
    throw "Release resource index not found: $resourceIndex"
}

$requiredRecordingFiles = @(
    (Join-Path $output "ScreenRecorderLib.dll"),
    (Join-Path $output "ThirdParty\THIRD-PARTY-NOTICES.md"),
    (Join-Path $output "ThirdParty\ScreenRecorderLib\LICENSE.txt"),
    (Join-Path $output "ThirdParty\FFmpeg\LICENSE.txt"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\ffmpeg.exe"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\avcodec-62.dll"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\avfilter-11.dll"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\avformat-62.dll"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\avutil-60.dll"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\swresample-6.dll"),
    (Join-Path $output "ThirdParty\FFmpeg\bin\swscale-9.dll")
)
$missingRecordingFiles = @($requiredRecordingFiles | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missingRecordingFiles.Count -gt 0) {
    throw "Recording runtime files are missing: $($missingRecordingFiles -join ', ')"
}
$ffmpeg = Join-Path $output "ThirdParty\FFmpeg\bin\ffmpeg.exe"
$ffmpegSignature = Get-AuthenticodeSignature -LiteralPath $ffmpeg
if ($ffmpegSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "FFmpeg executable signature is not valid: $($ffmpegSignature.Status)"
}
$ffmpegVersionOutput = @(& $ffmpeg -hide_banner -version 2>&1)
$ffmpegVersionExitCode = $LASTEXITCODE
$ffmpegVersion = [string]($ffmpegVersionOutput | Select-Object -First 1)
if ($ffmpegVersionExitCode -ne 0 -or -not $ffmpegVersion.StartsWith("ffmpeg version n8.1.2", [StringComparison]::Ordinal)) {
    throw "Unexpected or unusable FFmpeg runtime (exit=$ffmpegVersionExitCode): $ffmpegVersion"
}
$ffmpegEncoderOutput = @(& $ffmpeg -hide_banner -encoders 2>&1)
$ffmpegEncoderExitCode = $LASTEXITCODE
$ffmpegEncoders = $ffmpegEncoderOutput -join "`n"
if ($ffmpegEncoderExitCode -ne 0 -or $ffmpegEncoders -notmatch "(?m)\bgif\b" -or $ffmpegEncoders -notmatch "(?m)\blibwebp_anim\b") {
    throw "FFmpeg runtime does not provide the required GIF and animated WebP encoders."
}

$buildInputs = @(
    Get-ChildItem -LiteralPath (Join-Path $root "src") -Recurse -File |
        Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]'
)
$buildInputs += @(
    Join-Path $root "Directory.Build.props"
    Join-Path $root "global.json"
) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ }
$newestBuildInput = $buildInputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$executableItem = Get-Item -LiteralPath $executable
if ($newestBuildInput -and $newestBuildInput.LastWriteTimeUtc -gt $executableItem.LastWriteTimeUtc) {
    throw "Release output is stale: $($newestBuildInput.FullName) is newer than $executable. Rebuild Release x64."
}

$packagePath = Join-Path $root "artifacts\Tbox_${version}_x64.msix"
if (Test-Path -LiteralPath $packagePath) {
    throw "Package already exists: $packagePath. Increase the version before packaging."
}
$workingPackagePath = Join-Path $root "artifacts\.Tbox_${version}_x64.build.msix"
if (Test-Path -LiteralPath $workingPackagePath) {
    Remove-Item -LiteralPath $workingPackagePath -Force
}

$resolvedLayout = [IO.Path]::GetFullPath($layout)
$rootBoundary = $root.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) +
    [IO.Path]::DirectorySeparatorChar
if (-not $resolvedLayout.StartsWith($rootBoundary, [StringComparison]::OrdinalIgnoreCase)) {
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

$transactionId = [Guid]::NewGuid().ToString("N")
$historyTemp = "$history.$transactionId.tmp"
$historyBackup = "$history.$transactionId.bak"
$packagePromoted = $false
$historyReplaceAttempted = $false
$historyRollbackSucceeded = $false
$historyRollbackFailed = $false
$releaseCommitted = $false
try {
    & $makeAppx pack /d $layout /p $workingPackagePath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx failed with exit code $LASTEXITCODE."
    }

    $publisher = [string]$manifestXml.Package.Identity.Publisher
    $now = Get-Date
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $publisher -and
            $_.HasPrivateKey -and
            $_.NotBefore -le $now -and
            $_.NotAfter -gt $now -and
            @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }).Count -gt 0
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $certificate) {
        throw "A valid code-signing certificate matching manifest Publisher '$publisher' was not found."
    }

    & $signTool sign /sha1 $certificate.Thumbprint /fd SHA256 $workingPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed with exit code $LASTEXITCODE."
    }

    & $signTool verify /pa /v $workingPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX signature verification failed with exit code $LASTEXITCODE."
    }

    $packageHash = (Get-FileHash -LiteralPath $workingPackagePath -Algorithm SHA256).Hash
    $packagedAt = [DateTimeOffset]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
    $latestHistoryContent = Get-Content -LiteralPath $history -Raw -Encoding UTF8
    if (-not [string]::Equals($historyContent, $latestHistoryContent, [StringComparison]::Ordinal)) {
        throw "Release history changed while validation was running. Packaging was stopped without publishing an artifact."
    }
    $historyContent = $latestHistoryContent
    $lineEnding = if ($historyContent.Contains("`r`n")) { "`r`n" } else { "`n" }
    $updatedHistory = $historyContent.TrimEnd([char[]]"`r`n") +
        $lineEnding + "| $version | $packagedAt | ``$packageHash`` |" + $lineEnding
    [IO.File]::WriteAllText(
        $historyTemp,
        $updatedHistory,
        [Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $workingPackagePath -Destination $packagePath
    $packagePromoted = $true
    $promotedHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($packageHash, $promotedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Final package hash changed during promotion."
    }
    $historyReplaceAttempted = $true
    [IO.File]::Replace($historyTemp, $history, $historyBackup, $true)
    $releaseCommitted = $true
}
catch {
    $packagingError = $_
    if ($historyReplaceAttempted -and -not $releaseCommitted) {
        try {
            if (Test-Path -LiteralPath $historyBackup) {
                Copy-Item -LiteralPath $historyBackup -Destination $history -Force
            }
            else {
                [IO.File]::WriteAllText(
                    $history,
                    $historyContent,
                    [Text.UTF8Encoding]::new($false))
            }
            $historyRollbackSucceeded = $true
        }
        catch {
            $historyRollbackFailed = $true
            Write-Warning "Release-history rollback failed; preserved backup (if available): $historyBackup"
        }
    }
    if ($packagePromoted -and -not $historyRollbackFailed -and (Test-Path -LiteralPath $packagePath)) {
        Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
    }
    if ($historyRollbackFailed) {
        throw "Packaging failed and release-history rollback also failed. The verified package and backup were preserved for recovery. Original error: $packagingError"
    }
    throw $packagingError
}
finally {
    if (Test-Path -LiteralPath $workingPackagePath) {
        Remove-Item -LiteralPath $workingPackagePath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $historyTemp) {
        Remove-Item -LiteralPath $historyTemp -Force -ErrorAction SilentlyContinue
    }
    if (($releaseCommitted -or $historyRollbackSucceeded) -and (Test-Path -LiteralPath $historyBackup)) {
        Remove-Item -LiteralPath $historyBackup -Force -ErrorAction SilentlyContinue
    }
}

    Write-Host "Kept languages: $($Languages -join ', ')"
    Write-Host "Removed language directories: $($removedLanguages.Count)"
    Write-Host "SHA-256: $packageHash"
    Get-Item -LiteralPath $packagePath
}
finally {
    if ($packageMutexHeld) {
        $packageMutex.ReleaseMutex()
    }
    $packageMutex.Dispose()
}
