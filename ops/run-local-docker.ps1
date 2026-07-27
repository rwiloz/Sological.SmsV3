# Run Sological SMS v2 as a local Docker container — the Cloudflare-tunnel origin for
# SMS Central webhook callbacks (S3+). Safe to re-run: rebuilds the image and replaces the
# container. No secrets live in this file: the DB connection comes from the User-scoped
# SologicalSms__ConnectionStrings__DefaultConnection env var (host rewritten for the docker
# network), SMS Central creds come from the local Key Vault emulator.
#
# Port: 127.0.0.1:5230 -> container 8080 (HTTP; TLS terminates at the Cloudflare edge).
# Loopback-bound on purpose — run cloudflared on the HOST pointing at http://localhost:5230.
# If cloudflared runs as a container instead, attach it to the same network and target
# http://sologicalsms:8080 directly.

$ErrorActionPreference = "Stop"

$port = 5230
$network = "ai-workforce-dotnet_ai-workforce-network"   # where airflow-postgres (the shared PG) lives

$conn = [Environment]::GetEnvironmentVariable("SologicalSms__ConnectionStrings__DefaultConnection", "User")
if (-not $conn) { throw "SologicalSms__ConnectionStrings__DefaultConnection (User scope) is not set." }
$conn = $conn -replace "Host=localhost", "Host=airflow-postgres"

$vaultUri = "https://localhost:4997"
$headers = @{ Authorization = "Bearer eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJsb2NhbC1kZXYifQ." }
$user = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--SmsCentral--User?api-version=7.4" -Headers $headers -SkipCertificateCheck).value
$pass = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--SmsCentral--Password?api-version=7.4" -Headers $headers -SkipCertificateCheck).value
try { $verify = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--Ingress--VerifyKey?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $verify = "" }

docker build -t sologicalsms:dev $PSScriptRoot\..
docker rm -f sologicalsms 2>$null | Out-Null
docker run -d --name sologicalsms `
    --network $network `
    -p "127.0.0.1:${port}:8080" `
    -e "SologicalSms__ConnectionStrings__DefaultConnection=$conn" `
    -e "SologicalSms__SmsCentral__User=$user" `
    -e "SologicalSms__SmsCentral__Password=$pass" `
    -e "SologicalSms__Ingress__VerifyKey=$verify" `
    --restart unless-stopped `
    sologicalsms:dev

Write-Host "sologicalsms running on http://localhost:$port (health: /health)"
