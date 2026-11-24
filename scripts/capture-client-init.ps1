<#
.SYNOPSIS
    Connects each client library on this machine to DaffyTee once, runs SELECT @@VERSION, and keeps
    what DaffyTee recorded for each one.

.DESCRIPTION
    Starts its own DaffyTee on its own port with a throwaway config directory and every output on:
    the operational log (--log-file, which holds the LOGIN7 summary), the event file (--sink file),
    the SQL log (--sql-log) and one transcript per session (--transcript). Each client logs in as its
    own DaffyTee login, named after it, so every record says which client made it. Nothing but
    SELECT @@VERSION is run; anything else in the captures is the client's own doing.

    The clients are run one at a time, each in its own process, with its defaults: only the server,
    the login, encryption and trusting DaffyTee's self-signed certificate are set.

    Optional clients are skipped when what they need is not given: JDBC needs a Java runtime, the
    Microsoft JDBC driver jar and a compiler jar (ECJ) for its ten-line test; tedious needs its
    node_modules folder.

    The two drivers that ship with Windows (SQLSRV32 ODBC, SQLOLEDB) cannot be told to trust a
    self-signed certificate, so with encryption on they refuse DaffyTee's. -EncryptionOff connects
    them with Encrypt=no instead: DaffyTee, encryption mandatory, answers that encryption is
    required, and they encrypt the whole session without checking the certificate. -AllowPlaintext
    also starts DaffyTee with --allow-plaintext, so they are not encrypted at all (loopback only).

    Needs the test SQL Server from docker/compose.yml. docs/CLIENT-INIT.md was written from a run.
#>
param(
    [int]$Port = 14361,
    [string]$Target = "Server=tcp:127.0.0.1,14333;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;Encrypt=Mandatory;TrustServerCertificate=True",
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ("daffytee-client-init-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$Configuration = "Debug",
    [string]$Java = "C:\Program Files\DBeaver\jre\bin\java.exe",
    [string]$JdbcJar = "",
    [string]$Ecj = "",
    [string]$Tedious = "",
    [string[]]$Only = @(),
    [switch]$EncryptionOff,
    [switch]$AllowPlaintext
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$password = 'init-password'

function Wait-Port([int]$port, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return $true } catch { Start-Sleep -Milliseconds 200 }
    }
    return $false
}

# Runs a program and returns its exit code and everything it printed. Environment variables carry
# the connection details, so no password is on a command line.
function Invoke-Client([string]$exe, [string[]]$arguments) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $work
    $p = [Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(180000)) { $p.Kill() }
    [pscustomobject]@{ Exit = $p.ExitCode; Output = ($stdout.Result + $stderr.Result).Trim() }
}

New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $Out "work"
New-Item -ItemType Directory -Force $work | Out-Null
Write-Host "output in $Out"

# --- the small test programs ------------------------------------------------------------------

Set-Content -Encoding ASCII "$work\adonet.ps1" @'
param([string]$Type)
$ErrorActionPreference = 'Stop'
$cn = New-Object $Type ($env:INIT_CS)
try {
    $cn.Open()
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = 'SELECT @@VERSION'
    Write-Output (([string]$cmd.ExecuteScalar()) -split "`n")[0]
}
catch {
    $e = $_.Exception; if ($e.InnerException) { $e = $e.InnerException }
    [Console]::Error.WriteLine($e.Message); exit 1
}
finally { $cn.Close() }
'@

Set-Content -Encoding ASCII "$work\mds.cs" @'
#:package Microsoft.Data.SqlClient@6.1.1
using Microsoft.Data.SqlClient;

using var cn = new SqlConnection(Environment.GetEnvironmentVariable("INIT_CS"));
cn.Open();
using var cmd = new SqlCommand("SELECT @@VERSION", cn);
Console.WriteLine(((string)cmd.ExecuteScalar()!).Split('\n')[0]);
'@

Set-Content -Encoding ASCII "$work\JdbcVersion.java" @'
import java.sql.*;

public class JdbcVersion {
    public static void main(String[] args) throws Exception {
        try (Connection cn = DriverManager.getConnection(System.getenv("INIT_URL"), System.getenv("INIT_USER"), System.getenv("INIT_PASSWORD"));
             Statement st = cn.createStatement();
             ResultSet rs = st.executeQuery("SELECT @@VERSION")) {
            rs.next();
            System.out.println(rs.getString(1).split("\n")[0]);
        }
    }
}
'@

Set-Content -Encoding ASCII "$work\tedious.js" @'
const { Connection, Request } = require(process.env.TEDIOUS_PATH);
const cn = new Connection({
  server: '127.0.0.1',
  authentication: { type: 'default', options: { userName: process.env.INIT_USER, password: process.env.INIT_PASSWORD } },
  options: { port: Number(process.env.INIT_PORT), encrypt: true, trustServerCertificate: true }
});
cn.on('connect', err => {
  if (err) { console.error(err.message); process.exit(1); }
  const rq = new Request('SELECT @@VERSION', e => { if (e) { console.error(e.message); process.exitCode = 1; } cn.close(); });
  rq.on('row', cols => console.log(String(cols[0].value).split('\n')[0]));
  cn.execSql(rq);
});
cn.connect();
'@

# --- the clients ------------------------------------------------------------------------------

$server = "tcp:127.0.0.1,$Port"
$clients = @(
    @{ Name = 'mds611'; Label = 'Microsoft.Data.SqlClient 6.1.1 (.NET 10)'; Kind = 'mds'
       Cs = "Server=$server;User Id=@USER@;Password=@PW@;Encrypt=Mandatory;TrustServerCertificate=True" }
    @{ Name = 'sds_netfx'; Label = 'System.Data.SqlClient (.NET Framework 4.8, Windows PowerShell 5.1)'; Kind = 'ado'; Type = 'System.Data.SqlClient.SqlConnection'
       Cs = "Server=$server;User Id=@USER@;Password=@PW@;Encrypt=True;TrustServerCertificate=True" }
    @{ Name = 'odbc17'; Label = 'ODBC Driver 17 for SQL Server (System.Data.Odbc)'; Kind = 'ado'; Type = 'System.Data.Odbc.OdbcConnection'
       Cs = "Driver={ODBC Driver 17 for SQL Server};Server=$server;Uid=@USER@;Pwd=@PW@;Encrypt=yes;TrustServerCertificate=yes" }
    @{ Name = 'odbc18'; Label = 'ODBC Driver 18 for SQL Server (System.Data.Odbc)'; Kind = 'ado'; Type = 'System.Data.Odbc.OdbcConnection'
       Cs = "Driver={ODBC Driver 18 for SQL Server};Server=$server;Uid=@USER@;Pwd=@PW@;Encrypt=yes;TrustServerCertificate=yes" }
    @{ Name = 'odbc_legacy'; Label = 'SQL Server ODBC driver (SQLSRV32, ships with Windows)'; Kind = 'ado'; Type = 'System.Data.Odbc.OdbcConnection'
       Cs = "Driver={SQL Server};Server=$server;Uid=@USER@;Pwd=@PW@;Encrypt=yes;TrustServerCertificate=yes"
       PlainCs = "Driver={SQL Server};Server=$server;Uid=@USER@;Pwd=@PW@;Encrypt=no" }
    @{ Name = 'oledb19'; Label = 'OLE DB Driver 19 for SQL Server (MSOLEDBSQL19, System.Data.OleDb)'; Kind = 'ado'; Type = 'System.Data.OleDb.OleDbConnection'
       Cs = "Provider=MSOLEDBSQL19;Data Source=$server;User ID=@USER@;Password=@PW@;Use Encryption for Data=Mandatory;Trust Server Certificate=True" }
    @{ Name = 'oledb_legacy'; Label = 'SQLOLEDB provider (ships with Windows, System.Data.OleDb)'; Kind = 'ado'; Type = 'System.Data.OleDb.OleDbConnection'
       Cs = "Provider=SQLOLEDB;Data Source=$server;User ID=@USER@;Password=@PW@;Use Encryption for Data=True;Trust Server Certificate=True"
       PlainCs = "Provider=SQLOLEDB;Data Source=$server;User ID=@USER@;Password=@PW@;Use Encryption for Data=False" }
    @{ Name = 'sqlcmd_odbc17'; Label = 'sqlcmd 15.0 (classic, on ODBC Driver 17)'; Kind = 'sqlcmd'
       Exe = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'; Args = @('-N', '-C') }
    @{ Name = 'go_sqlcmd'; Label = 'go-sqlcmd (on go-mssqldb)'; Kind = 'sqlcmd'
       Exe = 'C:\Program Files\SqlCmd\sqlcmd.exe'; Args = @('-N', 'true', '-C') }
    @{ Name = 'jdbc'; Label = 'Microsoft JDBC Driver for SQL Server'; Kind = 'jdbc' }
    @{ Name = 'tedious'; Label = 'tedious (Node.js)'; Kind = 'tedious' }
)

# --- what can run here ------------------------------------------------------------------------

$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File passes "a,b" as one string
if ($Only.Count -gt 0) { $clients = @($clients | Where-Object { $Only -contains $_.Name }) }
if ($EncryptionOff -or $AllowPlaintext) {
    $how = if ($AllowPlaintext) { ', encryption off, DaffyTee --allow-plaintext' } else { ', Encrypt=no (DaffyTee requires encryption)' }
    foreach ($c in $clients | Where-Object { $_.PlainCs }) { $c.Cs = $c.PlainCs; $c.Label += $how }
}

$jdbc = $clients | Where-Object { $_.Name -eq 'jdbc' }
$tedi = $clients | Where-Object { $_.Name -eq 'tedious' }
$skip = @{}
if ($jdbc) {
    if (-not $JdbcJar) {
        $JdbcJar = Get-ChildItem "$env:APPDATA\DBeaverData\drivers" -Recurse -Filter 'mssql-jdbc-*.jre11.jar' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not ($Java -and (Test-Path $Java) -and $JdbcJar -and (Test-Path $JdbcJar) -and $Ecj -and (Test-Path $Ecj))) {
        $skip['jdbc'] = 'needs -Java, -JdbcJar and -Ecj'
    }
    else {
        $r = Invoke-Client $Java @('-jar', $Ecj, '-17', '-nowarn', '-d', "$work\classes", "$work\JdbcVersion.java")
        if ($r.Exit -ne 0) { throw "compiling the JDBC test failed: $($r.Output)" }
        $jdbcVersion = [regex]::Match((Split-Path -Leaf $JdbcJar), 'mssql-jdbc-([\d.]+)').Groups[1].Value
        $javaVersion = (& $Java --version | Select-Object -First 1) -replace '^\S+ (\S+).*', '$1'
        $jdbc.Label = "Microsoft JDBC Driver $jdbcVersion for SQL Server (Java $javaVersion)"
    }
}
if ($tedi) {
    if (-not ($Tedious -and (Test-Path "$Tedious\package.json"))) { $skip['tedious'] = 'needs -Tedious (the tedious module folder)' }
    else { $tedi.Label = "tedious $((Get-Content -Raw "$Tedious\package.json" | ConvertFrom-Json).version) (Node.js $(& node --version))" }
}
foreach ($c in $clients | Where-Object { $_.Kind -eq 'sqlcmd' }) {
    if (-not (Test-Path $c.Exe)) { $skip[$c.Name] = "not installed: $($c.Exe)" }
}

Write-Host "building DaffyTee ($Configuration)..."
dotnet build "$root/src/DaffyTee" -c $Configuration -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = "$root/src/DaffyTee/bin/$Configuration/net10.0/daffytee.exe"

if ($clients | Where-Object { $_.Kind -eq 'mds' }) {
    Write-Host "building the Microsoft.Data.SqlClient test..."
    $r = Invoke-Client 'dotnet' @('build', "$work\mds.cs", '-v', 'q', '-nologo')
    if ($r.Exit -ne 0) { throw "building mds.cs failed: $($r.Output)" }
}

# --- DaffyTee, with every output on --------------------------------------------------------------

$env:DAFFYTEE_HOME = "$work\home"
foreach ($c in $clients) {
    "$password`n$password`n" | & $exe user add $c.Name --target $Target | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "user add $($c.Name) failed" }
}

$proxyArgs = @('--port', $Port, '--no-repl', '--sink', 'file', '--sink-path', "$Out\events.ndjson", '--heartbeat', '0',
               '--log-file', "$Out\daffytee.log", '--sql-log', "$Out\sql.log", '--transcript', "$Out\transcripts")
if ($AllowPlaintext) { $proxyArgs += '--allow-plaintext' }
$proxy = Start-Process -FilePath $exe -PassThru -NoNewWindow -ArgumentList $proxyArgs `
    -RedirectStandardOutput "$work\proxy.out" -RedirectStandardError "$work\proxy.err"
$runs = @()
try {
    if (-not (Wait-Port $Port)) { throw "DaffyTee did not start listening on $Port" }

    foreach ($c in $clients) {
        if ($skip.ContainsKey($c.Name)) {
            Write-Host ("skip  {0,-14} {1}" -f $c.Name, $skip[$c.Name])
            $runs += [pscustomobject]@{ name = $c.Name; label = $c.Label; skipped = $skip[$c.Name] }
            continue
        }
        $env:INIT_USER = $c.Name
        $env:INIT_PASSWORD = $password
        $env:INIT_PORT = "$Port"
        $env:INIT_CS = if ($c.Cs) { $c.Cs -replace '@USER@', $c.Name -replace '@PW@', $password } else { '' }
        $env:INIT_URL = "jdbc:sqlserver://127.0.0.1:$Port;encrypt=true;trustServerCertificate=true"
        $env:TEDIOUS_PATH = $Tedious
        $env:SQLCMDPASSWORD = $password

        $started = (Get-Date).ToUniversalTime()
        $r = switch ($c.Kind) {
            'mds'     { Invoke-Client 'dotnet' @('run', "$work\mds.cs", '--no-build') }
            'ado'     { Invoke-Client 'powershell.exe' @('-NoProfile', '-NonInteractive', '-File', "$work\adonet.ps1", '-Type', $c.Type) }
            'sqlcmd'  { Invoke-Client $c.Exe (@('-S', $server, '-U', $c.Name) + $c.Args + @('-Q', 'SELECT @@VERSION')) }
            'jdbc'    { Invoke-Client $Java @('-cp', "$work\classes;$JdbcJar", 'JdbcVersion') }
            'tedious' { Invoke-Client 'node' @("$work\tedious.js") }
        }
        $ended = (Get-Date).ToUniversalTime()
        $first = ($r.Output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        Write-Host ("{0,-5} {1,-14} {2}" -f $(if ($r.Exit -eq 0) { 'ok' } else { 'FAIL' }), $c.Name, $first)
        $runs += [pscustomobject]@{ name = $c.Name; label = $c.Label; exit = $r.Exit; output = $r.Output; started = $started.ToString('o'); ended = $ended.ToString('o') }
        Start-Sleep -Milliseconds 1500   # let the session end before the next client starts
    }
    Start-Sleep -Seconds 3                # let the event file catch up
}
finally {
    Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:INIT_PASSWORD, Env:INIT_CS, Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
}

$runs | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 "$Out\runs.json"
Write-Host "captures in $Out"
