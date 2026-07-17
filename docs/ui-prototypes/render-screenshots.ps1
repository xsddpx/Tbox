[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$edge = @(
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $edge) {
    throw 'Microsoft Edge was not found.'
}

$prototypeDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$indexUri = [System.Uri]::new((Resolve-Path -LiteralPath (Join-Path $prototypeDirectory 'index.html')).Path).AbsoluteUri
$outputDirectory = Join-Path $prototypeDirectory 'screenshots'
$profileDirectory = Join-Path $env:TEMP 'tbox-ui-prototype-edge-profile'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null

$captures = @(
    @{ Name = 'tbox-ui-system-overview.png'; ProxyName = 'tbox-ui-system-overview.webp'; Query = '?page=system&capture=1'; Width = 1920; Height = 1080 },
    @{ Name = 'tbox-home-day-night.png'; Query = '?page=home&compare=1&capture=1'; Width = 1920; Height = 1080 },
    @{ Name = 'tbox-general-day-night.png'; Query = '?page=general&compare=1&capture=1'; Width = 1920; Height = 1080 },
    @{ Name = 'tbox-general-compact.png'; Query = '?page=general&sidebar=1&capture=1'; Width = 900; Height = 1080 },
    @{ Name = 'tbox-general-narrow.png'; Query = '?page=general&capture=1'; Width = 430; Height = 920 }
)

foreach ($capture in $captures) {
    $outputPath = Join-Path $outputDirectory $capture.Name
    $url = $indexUri + $capture.Query
    & $edge `
        --headless `
        --disable-gpu `
        --hide-scrollbars `
        --force-device-scale-factor=1 `
        --window-size="$($capture.Width),$($capture.Height)" `
        --virtual-time-budget=1500 `
        --user-data-dir="$profileDirectory" `
        --screenshot="$outputPath" `
        $url | Out-Null

    if (-not (Test-Path -LiteralPath $outputPath)) {
        throw "Screenshot generation failed: $outputPath"
    }

    if ($capture.ProxyName) {
        $ffmpeg = Join-Path $prototypeDirectory '..\..\src\Tbox.App\ThirdParty\FFmpeg\bin\ffmpeg.exe'
        $ffmpeg = (Resolve-Path -LiteralPath $ffmpeg).Path
        $proxyPath = Join-Path $outputDirectory $capture.ProxyName
        & $ffmpeg -y -loglevel error -i $outputPath -c:v libwebp -quality 84 -compression_level 6 $proxyPath
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $proxyPath)) {
            throw "Preview proxy generation failed: $proxyPath"
        }
        Remove-Item -LiteralPath $outputPath
    }
}

$previewFiles = Get-ChildItem -LiteralPath $outputDirectory -File |
    Where-Object { $_.Extension -in '.png', '.webp' } |
    Sort-Object Name

$oversizedFiles = $previewFiles | Where-Object Length -GE 250KB
if ($oversizedFiles) {
    throw "Preview files must be smaller than 250KB: $($oversizedFiles.Name -join ', ')"
}

$previewFiles | Select-Object Name, Length, LastWriteTime
