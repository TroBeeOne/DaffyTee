#!/usr/bin/env bash
# Linux smoke test, run inside the .NET SDK container by scripts/verify-linux.ps1:
#
#   docker run --rm -v <repo>/nupkg:/nupkg:ro -v <TeeProbe bin>:/probe:ro mcr.microsoft.com/dotnet/sdk:10.0 bash /scripts/linux-smoke.sh
#
# Installs DaffyTee from the local package exactly as a user would, creates a mapping (checking the
# key file is owner-only), runs the proxy with a file sink, and points TeeProbe - SqlClient's managed
# SNI, not the Windows native one - at it. The SQL Server is the test container on the Docker host.
set -euo pipefail

SQL="${SQL_HOST:-host.docker.internal},${SQL_PORT:-14333}"
TARGET="Server=tcp:$SQL;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;Encrypt=Mandatory;TrustServerCertificate=True"
failures=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; failures=$((failures+1)); fi; }

dotnet tool install --tool-path /tools DaffyTee --add-source /nupkg --version "${VERSION:?set VERSION to the package version}" > /dev/null
/tools/daffytee --version

printf 'linuxpass1\nlinuxpass1\n' | /tools/daffytee user add linux --target "$TARGET"
KEY="$HOME/.local/share/DaffyTee/store.key"
check "the store key is owner-only (0600)" '[ "$(stat -c %a "$KEY")" = "600" ]'
check "the store is not readable text" '! grep -q svc_app "$HOME/.local/share/DaffyTee/users.dat"'
printf 'plainpass1\nplainpass1\n' | /tools/daffytee user add plain --target "$TARGET" > /dev/null
/tools/daffytee user admin linux on > /dev/null

/tools/daffytee --port 14340 --no-repl --sink file --sink-path /tmp/events.ndjson --heartbeat 0 > /tmp/proxy.log 2>&1 &
PROXY=$!
for i in $(seq 1 50); do (echo > /dev/tcp/127.0.0.1/14340) 2>/dev/null && break; sleep 0.2; done

DIRECT="Server=tcp:$SQL;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False"
set +e
dotnet /probe/TeeProbe.dll --port 14340 --user linux --password linuxpass1 \
    --cert "$HOME/.local/share/DaffyTee/certs/daffytee.cer" --direct "$DIRECT" --gateway admin --non-admin plain:plainpass1
PROBE=$?
set -e
check "every TeeProbe check passes on Linux (managed SNI, OpenSSL)" '[ $PROBE -eq 0 ]'

sleep 2
REQUESTS=$(grep -c '"type":"request\.' /tmp/events.ndjson || true)
INCOMPLETE=$(grep -c '"status":"incomplete"' /tmp/events.ndjson || true)
check "requests were captured ($REQUESTS)" '[ "$REQUESTS" -ge 50 ]'
check "every request was paired with its response" '[ "$INCOMPLETE" -eq 0 ]'
check "daffytee.* commands are captured as DaffyTee's own answers" 'grep -q "\"gatewayCommand\":\"daffytee.status\"" /tmp/events.ndjson'
check "a password given to a command is masked" '! grep -q "Secret#Probe1" /tmp/events.ndjson'

# Trusting the certificate is Windows-only: here it says so, changes nothing, and is not offered.
CERT_OUT=$(/tools/daffytee cert)
check "cert shows the certificate, trust not checked on Linux" 'echo "$CERT_OUT" | grep -q "trusted      not checked"'
set +e
TRUST_OUT=$(/tools/daffytee cert trust); TRUST=$?
set -e
check "cert trust declines on Linux" '[ $TRUST -ne 0 ] && echo "$TRUST_OUT" | grep -q "only done on Windows"'
check "the banner does not offer it" '! grep -q "clients that check the certificate" /tmp/proxy.log'

kill $PROXY
echo
if [ $failures -gt 0 ]; then echo "$failures check(s) FAILED"; tail -20 /tmp/proxy.log; exit 1; fi
echo "all Linux checks passed"
