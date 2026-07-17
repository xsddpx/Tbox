[CmdletBinding()]
param(
    [int]$Port = 4173
)

$ErrorActionPreference = 'Stop'
$prototypeDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    $python = Get-Command py -ErrorAction SilentlyContinue
}
if (-not $python) {
    throw 'Python was not found. Install Python or open index.html directly in a browser.'
}

$existingListener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $existingListener) {
    $arguments = @('-m', 'http.server', $Port, '--bind', '127.0.0.1', '--directory', $prototypeDirectory)
    Start-Process -FilePath $python.Source -ArgumentList $arguments -WindowStyle Hidden | Out-Null

    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 100
        if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
            $ready = $true
            break
        }
    }
    if (-not $ready) {
        throw "Prototype server did not start on port $Port."
    }
}

Write-Output "http://127.0.0.1:$Port/?page=general"
