<#
.SYNOPSIS
    End-to-end check of the relay and the tee: a real proxy, a real client, a real SQL Server.

.DESCRIPTION
    Builds the solution, creates a user mapping in a throwaway config directory (answering the
    prompts through stdin, as any script would), starts DaffyTee on its own port with a file sink,
    runs every TeeProbe check through it, and then asserts on the events it wrote.

    It needs the test SQL Server from docker/compose.yml, seeded with docker/seed.sql. It starts
    and stops its own proxy, so it will not disturb one already running.

    Asserts on what the run must produce - events present, statuses right - never merely on the
    absence of an error, which a run that never connected would also satisfy.
#>
param(
    [int]$Port = 14350,
    [string]$Target = "Server=tcp:127.0.0.1,14333;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;Encrypt=Mandatory;TrustServerCertificate=True",
    [string]$TargetLogin = "svc_app",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

function Wait-Port([int]$port, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 200 }
    }
    return $false
}

Write-Host "building ($Configuration)..."
dotnet build "$root/DaffyTee.slnx" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = "$root/src/DaffyTee/bin/$Configuration/net10.0/daffytee.exe"
$probe = "$root/probes/TeeProbe/bin/$Configuration/net10.0/TeeProbe.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("daffytee-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$env:DAFFYTEE_HOME = "$work\home"
Write-Host "working in $work"

# A mapping, made the way a script makes one: the answers piped to the prompts.
"verify-password`nverify-password`n" | & $exe user add verify --target $Target | Out-Host
Check "user add validates the target and saves" ($LASTEXITCODE -eq 0)

"x`nx`n" | & $exe user add broken --target ($Target -replace 'Password=[^;]*', 'Password=wrong') | Out-Null
Check "user add refuses a target that will not log in" ($LASTEXITCODE -ne 0)

# A DaffyTee admin, for the daffytee.* commands, and an ordinary login to be refused them.
"plain-password`nplain-password`n" | & $exe user add plain --target $Target | Out-Null
& $exe user admin verify on | Out-Null
Check "user admin makes a login a DaffyTee admin" (($LASTEXITCODE -eq 0) -and ((& $exe user show verify | Out-String) -match "admin\s+: yes"))

$events = "$work\events.ndjson"
$proxy = Start-Process -FilePath $exe -PassThru -NoNewWindow `
    -ArgumentList @('--port', $Port, '--no-repl', '--sink', 'file', '--sink-path', $events, '--heartbeat', '0', '--log-file', "$work\daffytee.log") `
    -RedirectStandardOutput "$work\proxy.out" -RedirectStandardError "$work\proxy.err"
try {
    Check "proxy listens on $Port" (Wait-Port $Port)

    # This run's certificate is brand new, so Windows cannot trust it: a client that checks it must
    # be refused (--expect-trust no). The other half, a trusted certificate, needs a person to answer
    # Windows' own confirmation, so it is checked by hand.
    $output = & $probe --port $Port --user verify --password verify-password --target-login $TargetLogin --cert "$work\home\certs\daffytee.cer" --expect-trust no `
        --gateway admin --non-admin 'plain:plain-password' 2>&1
    $output | Out-Host
    Check "every TeeProbe check passes through the proxy" ($LASTEXITCODE -eq 0) ($output | Select-String 'FAIL' | Out-String)

    # The ODBC driver that ships with Windows (SQLSRV32) speaks TDS 7.1: an 86-byte LOGIN7 fixed
    # part, 4-byte DONE row counts. With Encrypt=no it encrypts the whole session when DaffyTee
    # requires it; with Encrypt=yes it checks the certificate, cannot be told to trust a self-signed
    # one, and hangs up after TLS. Run in Windows PowerShell, which always has System.Data.Odbc.
    $odbc = {
        param($cs)
        $cn = New-Object System.Data.Odbc.OdbcConnection $cs
        $cn.ConnectionTimeout = 3
        try { $cn.Open(); $c = $cn.CreateCommand(); $c.CommandText = 'SELECT 41 + 1'; "ok $($c.ExecuteScalar())" }
        catch { "refused: $($_.Exception.InnerException.Message)" }
        finally { $cn.Close() }
    }
    $legacy = "Driver={SQL Server};Server=tcp:127.0.0.1,$Port;Uid=verify;Pwd=verify-password;App=LegacyOdbc"
    $legacyOk = & powershell.exe -NoProfile -NonInteractive -Command $odbc -args "$legacy;Encrypt=no" | Out-String
    Check "the Windows ODBC driver (TDS 7.1) logs in and runs a query" ($legacyOk -match "ok 42") $legacyOk
    $legacyTls = & powershell.exe -NoProfile -NonInteractive -Command $odbc -args "$legacy;Encrypt=yes" | Out-String
    Check "the Windows ODBC driver with Encrypt=yes refuses the self-signed certificate" ($legacyTls -match "refused") $legacyTls

    Start-Sleep -Seconds 2
    $all = Get-Content -Encoding UTF8 $events | ForEach-Object { $_ | ConvertFrom-Json }
    $requests = @($all | Where-Object { $_.type -like 'request.*' })

    Check "requests were captured" ($requests.Count -ge 50) "only $($requests.Count)"
    Check "every event is CloudEvents 1.0, schema v1" (@($all | Where-Object { $_.specversion -ne '1.0' -or $_.data.v -ne 1 }).Count -eq 0)
    Check "every request was paired with its response" (@($requests | Where-Object { $_.data.response.status -eq 'incomplete' }).Count -eq 0)
    Check "errors are reported as errors" (@($requests | Where-Object { $_.data.response.status -eq 'error' }).Count -ge 2)
    Check "cancel and timeout are reported as cancelled" (@($requests | Where-Object { $_.data.response.status -eq 'cancelled' }).Count -ge 2)
    Check "refused logins are events, with reasons" (@($all | Where-Object { $_.type -eq 'session.login_failed' -and $_.data.reason -in @('bad_password', 'unknown_user') }).Count -eq 2)
    Check "session start and end are paired" (@($all | Where-Object { $_.type -eq 'session.start' }).Count -eq @($all | Where-Object { $_.type -eq 'session.end' }).Count)
    Check "the target login and SPID are recorded" (@($requests | Where-Object { $_.data.session.targetLogin -ne $TargetLogin -or $_.data.session.spid -le 0 }).Count -eq 0)
    Check "sp_executesql parameters are typed and valued" (@($requests | Where-Object { $_.data.request.calls.params | Where-Object { $_.name -eq '@s' -and $_.value -eq "h$([char]0xE9)llo" -and $_.type -eq 'nvarchar(50)' } }).Count -eq 1)
    # From the file's text: PowerShell 7's ConvertFrom-Json turns the value into a DateTime.
    Check "datetime parameters keep their time of day, to the millisecond" ((Select-String -Path $events -Encoding UTF8 -Pattern '"name":"@dtm","type":"datetime",[^}]*"value":"2026-09-23T13:14:15\.123"').Count -eq 1)
    Check "table-valued parameters come through as rows" (@($requests | Where-Object { $_.data.request.calls.params.value.tableType -eq 'dbo.IdList' }).Count -eq 1)
    Check "output parameters come back in the response" (@($requests | Where-Object { $_.data.response.output.'@Name' -eq 'Grace Hopper' }).Count -ge 1)
    Check "sp_execute is resolved to its prepared statement" (@($requests | Where-Object { $_.data.request.calls.proc -eq 'sp_execute' -and $_.data.request.calls.sql -like 'SELECT Name*' }).Count -ge 2)
    Check "SqlTransaction begin/commit/rollback/save are decoded" (@($requests | Where-Object { $_.type -eq 'request.txn' } | ForEach-Object { $_.data.request.transaction.op } | Sort-Object -Unique).Count -ge 4)
    Check "bulk loads are counted, with their table" (@($requests | Where-Object { $_.type -eq 'request.bulk' -and $_.data.request.bulk.table -like '*BulkTarget*' -and $_.data.response.rowCount -gt 0 }).Count -ge 1)
    Check "MARS requests carry their channel" (@($requests | Where-Object { $null -ne $_.data.request.marsChannel }).Count -ge 2)
    Check "USE is followed into the next events" (@($requests | Where-Object { $_.data.session.database -eq 'DaffyTeeOther' }).Count -ge 1)
    Check "the operational log is JSON lines" (@(Get-Content -Encoding UTF8 "$work\daffytee.log" | ForEach-Object { ($_ | ConvertFrom-Json).event }) -contains 'session.open')

    # TDS 7.1, end to end: its own security label, the query decoded, the final DONE read at 9 bytes.
    $legacyEvents = @($all | Where-Object { $_.data.session.app -eq 'LegacyOdbc' })
    Check "a TDS 7.1 session is labelled as such" (@($legacyEvents | Where-Object { $_.type -eq 'session.start' -and $_.data.clientSecurity -eq 'Optional->upgraded (TDS 7.1), Tls12' }).Count -eq 1)
    Check "a TDS 7.1 request is decoded, row count included" (@($legacyEvents | Where-Object { $_.type -eq 'request.batch' -and $_.data.request.sql -eq 'SELECT 41 + 1' -and $_.data.response.rowCount -eq 1 -and $null -eq $_.data.response.detailProblem }).Count -eq 1)
    $logLines = @(Get-Content -Encoding UTF8 "$work\daffytee.log" | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.event -eq 'session.closed_early' })
    Check "a connection that sends nothing is logged with a reason" (@($logLines | Where-Object { $_.msg -like '*without sending PRELOGIN*' }).Count -ge 1)
    Check "a client that hangs up after TLS is logged with a reason" (@($logLines | Where-Object { $_.msg -like '*after the TLS handshake*certificate*' }).Count -ge 1)

    # daffytee.* commands: answered by DaffyTee, and on the record like any request.
    Check "commands are captured, marked as DaffyTee's own answers" (@($requests | Where-Object { $_.data.request.gatewayCommand -eq 'daffytee.status' -and $_.data.response.status -eq 'ok' }).Count -ge 5)
    Check "a refused admin command is on the record" (@($requests | Where-Object { $_.data.request.gatewayCommand -eq 'daffytee.sessions' -and $_.data.session.login -eq 'plain' -and $_.data.response.status -eq 'error' }).Count -ge 1)
    Check "a password given to a command is masked" ((@($requests | Where-Object { $_.data.request.gatewayCommand -eq 'daffytee.set_user_password' -and $_.data.request.redacted }).Count -eq 1) -and
        -not (Select-String -Path $events -Pattern 'Secret#Probe1' -SimpleMatch -Quiet))
    Check "a session ended with daffytee.kill_session says who ended it" (@($all | Where-Object { $_.type -eq 'session.end' -and $_.data.reason -like "killed by 'verify'*daffytee.kill_session" }).Count -eq 1)
    Check "admin commands are in the operational log" (@(Get-Content -Encoding UTF8 "$work\daffytee.log" | ForEach-Object { ($_ | ConvertFrom-Json).event }) -contains 'admin.command')

    # The certificate: what the banner says, what 'cert' says, and that nothing is asked of a script.
    $banner = Get-Content -Raw "$work\proxy.out"
    Check "the banner says what clients that check the certificate need" ($banner -match "other clients that check the certificate[\s\S]+once the certificate is trusted: daffytee cert trust")
    Check "the banner says where to start in T-SQL" ($banner -match "In T-SQL\s+: EXEC daffytee\.help\s+\(admins: verify\)")
    $cert = & $exe cert | Out-String
    Check "'cert' shows the certificate and that it is not trusted" (($cert -match "names\s+localhost") -and ($cert -match "trusted\s+no - "))
    # Only where nothing is trusted: removing a trusted one would stop the run for Windows' confirmation.
    if (@(Get-ChildItem Cert:\CurrentUser\Root | Where-Object FriendlyName -eq 'DaffyTee (self-signed, for local connections)').Count -eq 0) {
        $untrust = & $exe --untrust-cert | Out-String
        Check "--untrust-cert with nothing trusted says so" (($LASTEXITCODE -eq 0) -and ($untrust -match "No DaffyTee certificate was trusted"))
    }
    else { Write-Host "skip  --untrust-cert (a DaffyTee certificate is trusted for this user; left alone)" }
    $repl = ":cert`n:quit`n" | & $exe --port ($Port + 1) --repl | Out-String
    Check ":cert works at the prompt" ($repl -match "\(generated by DaffyTee\)")
    Check "and a prompt reading a script never asks to trust" ($repl -notmatch "Trust it now")
}
finally {
    Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) FAILED - evidence kept in $work" -ForegroundColor Red
    exit 1
}
Write-Host "all checks passed"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
exit 0
