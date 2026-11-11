# Run Sological SMS v3 as a local Docker container — the Cloudflare-tunnel origin
# (sms-yoga.sological.io) for upstream webhooks: Mobile Message's, forwarded by the legacy
# app's router under the `local` tag, and SMS Central's (S3+). Safe to re-run: rebuilds the image and replaces the
# container. No secrets live in this file: the DB connection comes from the User-scoped
# SologicalSms__ConnectionStrings__DefaultConnection env var (host rewritten for the docker
# network), upstream creds come from the local Key Vault emulator.
#
# Port: 127.0.0.1:5230 -> container 8080 (HTTP; TLS terminates at the Cloudflare edge).
# Loopback-bound on purpose — run cloudflared on the HOST pointing at http://localhost:5230.
# If cloudflared runs as a container instead, attach it to the same network and target
# http://sologicalsms:8080 directly.

$ErrorActionPreference = "Stop"

$port = 5230
$network = "ai-workforce-dotnet_ai-workforce-network"   # where airflow-postgres (the shared PG) lives
$pgHost = "airflow-postgres"

$conn = [Environment]::GetEnvironmentVariable("SologicalSms__ConnectionStrings__DefaultConnection", "User")
if (-not $conn) { throw "SologicalSms__ConnectionStrings__DefaultConnection (User scope) is not set." }
$conn = $conn -replace "Host=localhost", "Host=$pgHost"

# The service fails hard when the database is unreachable at startup. When Docker restarts the
# container itself (Docker Desktop restart), the shared PG may not be up yet, so wait for its port
# before starting. A PG still recovering ("starting up") can cost one more restart; the policy covers it.
$startCmd = "until (echo > /dev/tcp/$pgHost/5432) 2>/dev/null; do sleep 2; done; exec dotnet Sological.Sms.Service.dll"

$vaultUri = "https://localhost:4997"
$headers = @{ Authorization = "Bearer eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJsb2NhbC1kZXYifQ." }
$user = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--SmsCentral--User?api-version=7.4" -Headers $headers -SkipCertificateCheck).value
$pass = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--SmsCentral--Password?api-version=7.4" -Headers $headers -SkipCertificateCheck).value
try { $verify = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--Ingress--VerifyKey?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $verify = "" }
try { $aiwWebhook = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--Webhook--AIWorkforce?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $aiwWebhook = "" }
try { $adminKey = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--Admin--ApiKey?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $adminKey = "" }
# Mobile Message (the default upstream): this machine's own API key pair and the account's webhook signing
# secret. Missing values leave sends held (not_configured) and webhooks answered 503, never sent wrongly.
try { $mmUser = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--MobileMessage--ApiUsername?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $mmUser = "" }
try { $mmPass = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--MobileMessage--ApiPassword?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $mmPass = "" }
try { $mmSigning = (Invoke-RestMethod -Method Get -Uri "$vaultUri/secrets/SologicalSms--MobileMessage--SigningSecret?api-version=7.4" -Headers $headers -SkipCertificateCheck).value } catch { $mmSigning = "" }

docker build -t sologicalsms:dev $PSScriptRoot\..
docker rm -f sologicalsms 2>$null | Out-Null
docker run -d --name sologicalsms `
    --network $network `
    -p "127.0.0.1:${port}:8080" `
    -e "SologicalSms__ConnectionStrings__DefaultConnection=$conn" `
    -e "SologicalSms__SmsCentral__User=$user" `
    -e "SologicalSms__SmsCentral__Password=$pass" `
    -e "SologicalSms__Ingress__VerifyKey=$verify" `
    -e "SologicalSms__Ingress__RequireVerification=true" `
    -e "SologicalSms__Webhook__AIWorkforce=$aiwWebhook" `
    -e "SologicalSms__Admin__ApiKey=$adminKey" `
    -e "SologicalSms__MobileMessage__ApiUsername=$mmUser" `
    -e "SologicalSms__MobileMessage__ApiPassword=$mmPass" `
    -e "SologicalSms__MobileMessage__SigningSecret=$mmSigning" `
    -e "SologicalSms__MobileMessage__EnvironmentTag=local" `
    --restart unless-stopped `
    --entrypoint bash `
    sologicalsms:dev -c $startCmd

Write-Host "sologicalsms running on http://localhost:$port (health: /health)"
