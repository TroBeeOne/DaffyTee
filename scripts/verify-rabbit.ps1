<#
.SYNOPSIS
    Capture into RabbitMQ, then take RabbitMQ away under load and prove the database never noticed.

.DESCRIPTION
    Part one publishes a run of TeeProbe checks to a fresh queue and validates every message
    (CloudEvents envelope, routing key = type, message id = event id).

    Part two runs steady load through the proxy, stops the RabbitMQ container part-way, starts it
    again, and asserts: zero client errors; the proxy logged the outage, the queue filling and the
    recovery; and consumers received gap markers accounting for what was dropped.

    Needs docker/compose.yml up with --profile rabbit, and the SQL Server seeded.
#>
param(
    [int]$Port = 14351,
    [string]$Target = "Server=tcp:127.0.0.1,14333;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;Encrypt=Mandatory;TrustServerCertificate=True",
    [string]$Rabbit = "amqp://guest:guest@localhost:5672/",
    [string]$RabbitContainer = "daffytee-rabbit-1",
    [int]$OutageSeconds = 10,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function Wait-Port([int]$port, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

# Docker Desktop's CLI is not always on PATH in a shell opened before it was installed.
$docker = (Get-Command docker -ErrorAction SilentlyContinue).Source
if (-not $docker) { $docker = "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe" }
if (-not (Test-Path $docker)) { $docker = "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe" }

Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyTee.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = "$root/src/DaffyTee/bin/$Configuration/net10.0/daffytee.exe"
$probe = "$root/probes/TeeProbe/bin/$Configuration/net10.0/TeeProbe.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffytee-verify-rabbit-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$queue = "verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYTEE_HOME = "$work\home"
Write-Host "working in $work, queue $queue"

"verify-password`nverify-password`n" | & $exe user add verify --target $Target | Out-Null
Check "user mapping created" ($LASTEXITCODE -eq 0)

$proxy = Start-Process -FilePath $exe -PassThru -NoNewWindow `
    -ArgumentList @('--port', $Port, '--no-repl', '--sink', 'rabbit', '--rabbit', $Rabbit, '--rabbit-queue', $queue,
                    '--queue-max-events', '2000', '--heartbeat', '5', '--log-file', "$work\daffytee.log") `
    -RedirectStandardOutput "$work\proxy.out" -RedirectStandardError "$work\proxy.err"

function Consume() {
    $lines = & $probe --consume $Rabbit --queue $queue 2>&1
    $summary = $lines | Select-String 'messages=(\d+) invalid=(\d+) duplicates=(\d+) gaps=(\d+)'
    if (-not $summary) { return @{ Messages = 0; Invalid = -1; Gaps = 0; Text = ($lines | Out-String) } }
    $m = $summary.Matches[0].Groups
    return @{ Messages = [int]$m[1].Value; Invalid = [int]$m[2].Value; Gaps = [int]$m[4].Value; Text = ($lines | Out-String) }
}

try {
    Check "proxy listens on $Port" (Wait-Port $Port)
    Start-Sleep -Seconds 2   # connect and declare the exchange and queue

    # ---- part one: every message well formed ----------------------------------------------------
    foreach ($check in 'parameters', 'tvp', 'transactions', 'prepared', 'mars', 'bulk-copy') {
        & $probe --port $Port --user verify --password verify-password --only $check | Out-Null
    }
    Start-Sleep -Seconds 2
    $first = Consume
    Write-Host $first.Text
    Check "events arrived in RabbitMQ" ($first.Messages -ge 20) "got $($first.Messages)"
    Check "every message is a valid event (envelope, routing key, message id)" ($first.Invalid -eq 0) $first.Text

    # ---- part two: the broker goes away under load ---------------------------------------------
    $loadSeconds = $OutageSeconds + 15
    $load = Start-Process -FilePath $probe -PassThru -NoNewWindow `
        -ArgumentList @('--port', $Port, '--user', 'verify', '--password', 'verify-password', '--only', 'identity', '--load', $loadSeconds) `
        -RedirectStandardOutput "$work\load.out" -RedirectStandardError "$work\load.err"

    Start-Sleep -Seconds 5
    Write-Host "stopping $RabbitContainer for $OutageSeconds s, under load"
    & $docker stop $RabbitContainer | Out-Null
    Start-Sleep -Seconds $OutageSeconds
    & $docker start $RabbitContainer | Out-Null
    Write-Host "started $RabbitContainer again"

    $load.WaitForExit()
    $loadText = Get-Content "$work\load.out" | Out-String
    Write-Host ($loadText -split "`n" | Select-Object -Last 3 | Out-String)
    Check "the load saw no errors while RabbitMQ was down" ($loadText -match 'load done: \d+ requests, 0 errors') $loadText

    # Give the proxy time to notice the broker is back (retries back off to 10 s) and deliver.
    Wait-Port 5672 60 | Out-Null
    Start-Sleep -Seconds 15

    $log = Get-Content -Encoding UTF8 "$work\daffytee.log" | ForEach-Object { ($_ | ConvertFrom-Json).event }
    Check "the outage was logged" ($log -contains 'sink.unavailable')
    Check "the queue filling was logged" (($log -contains 'queue.backlog') -or ($log -contains 'queue.dropping'))
    Check "the recovery was logged" ($log -contains 'sink.recovered')
    Check "the outage was logged once, not per retry" (@($log | Where-Object { $_ -eq 'sink.unavailable' }).Count -eq 1)

    $second = Consume
    Write-Host $second.Text
    Check "events flowed again after the outage" ($second.Messages -gt 0)
    Check "drops were reported to consumers as gap markers" ($second.Gaps -ge 1) $second.Text
    Check "every message after the outage is valid" ($second.Invalid -eq 0)
}
finally {
    Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue
    & $docker exec $RabbitContainer rabbitmqctl delete_queue $queue 2>&1 | Out-Null
}

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED - evidence kept in $work" -ForegroundColor Red
    exit 1
}
Write-Host "all checks passed"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
exit 0
