param([Parameter(Mandatory)][string]$ResultDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Use a disposable GitHub-hosted runner for the test enrollment.'
}

function Get-ConsoleWorkers {
    Get-CimInstance Win32_Process -Filter "Name='TboxRemote.exe'" |
        Where-Object { $_.CommandLine -match ' --server --tbox-console [0-9]+$' }
}

$directory = Join-Path ([Environment]::SystemDirectory) 'config\systemprofile\AppData\Local\TboxRemoteHost'
if (Test-Path -LiteralPath (Join-Path $directory 'active.json')) { throw 'A real host is enabled on the test machine.' }
if (@(Get-ConsoleWorkers).Count) { throw 'A console worker started before local approval.' }
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$attempt = [Guid]::NewGuid().ToString('D')
$pending = Join-Path $directory 'pending.json'
$draft = Join-Path $directory 'test-pending.new'
$identityPath = Join-Path $directory "identity-$attempt.json"
if (Test-Path -LiteralPath $pending) { throw 'An existing pending enrollment is present.' }
try {
    $app = Get-AppxPackage -Name Tbox
    $policy = Get-Content -LiteralPath (Join-Path $app.InstallLocation 'Remote\tbox-host-policy.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $random = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $credential = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $fixture = [ordered]@{ attempt = $attempt; deviceId = [Guid]::NewGuid().ToString('D');
        credential = $credential; expiresAt = [DateTimeOffset]::UtcNow.AddSeconds(60).ToUnixTimeSeconds(); policy = $policy }
    $fixture.policy = @{ origin = 'https://example.invalid/'; network = $policy.network }
    [IO.File]::WriteAllText($draft, ($fixture | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($draft, $pending)
    Start-Sleep -Seconds 2
    if (@(Get-ConsoleWorkers).Count -or (Test-Path -LiteralPath $identityPath)) {
        throw 'A request with a different account origin prepared a host.'
    }
    Remove-Item -LiteralPath $pending
    $fixture.policy = $policy
    [IO.File]::WriteAllText($draft, ($fixture | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($draft, $pending)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    while (-not (Test-Path -LiteralPath $identityPath)) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'The packaged service did not launch the pending console worker.' }
        Start-Sleep -Milliseconds 250
    }
    $workers = @(Get-ConsoleWorkers)
    if ($workers.Count -ne 1 -or $workers[0].SessionId -eq 0) { throw 'The engine is not running in the active console session.' }
    $service = Get-CimInstance Win32_Service -Filter "Name='TboxRemote'"
    if ($workers[0].ParentProcessId -ne $service.ProcessId) { throw 'The console worker was not started by the installed Tbox service.' }
    $owner = Invoke-CimMethod -InputObject $workers[0] -MethodName GetOwnerSid
    if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne 'S-1-5-18') { throw 'The console worker is not running as LocalSystem.' }
    $identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($identity.peerId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{5,31}$' -or
        [Convert]::FromBase64String($identity.publicKey).Length -ne 32) { throw 'The console worker did not publish its public identity.' }
    if (Test-Path -LiteralPath (Join-Path $directory 'active.json')) { throw 'Preparing an identity activated an unapproved host.' }
    Remove-Item -LiteralPath $pending
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(8)
    while (@(Get-ConsoleWorkers).Count) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Cancelling consent did not stop the worker.' }
        Start-Sleep -Milliseconds 250
    }
    [ordered]@{ disabledByDefault = $true; differentOriginRejected = $true; localSystemConsoleWorker = $true; pendingConsentDidNotActivate = $true;
        cancelStoppedWorker = $true } | ConvertTo-Json |
        Set-Content (Join-Path $ResultDirectory 'remote-console.json') -Encoding UTF8
} catch {
    $failure = $_
    try {
        [ordered]@{
            workerSessions = @(Get-ConsoleWorkers | ForEach-Object SessionId)
            winlogonSessions = @(Get-Process -Name winlogon | ForEach-Object SessionId)
            identityPublished = Test-Path -LiteralPath $identityPath
            serviceStage = [string](Get-Content -LiteralPath (Join-Path $directory 'service-status.txt') -Raw -ErrorAction SilentlyContinue)
            consoleStage = [string](Get-Content -LiteralPath (Join-Path $directory 'console-status.txt') -Raw -ErrorAction SilentlyContinue)
        } | ConvertTo-Json | Set-Content (Join-Path $ResultDirectory 'remote-console-failure.json') -Encoding UTF8
    } catch {
        Write-Warning 'Could not collect Windows console worker diagnostics.'
    }
    throw $failure
} finally {
    Remove-Item -LiteralPath $draft, $pending, $identityPath -Force -ErrorAction SilentlyContinue
}
