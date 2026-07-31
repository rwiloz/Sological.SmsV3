// Sological SMS v3 — Azure DEV Container App deployment.
// ALL SHARED resources (ruled 2026-07-31): deploys into the existing AI-Workforce
// Container Apps environment, pulls from the shared ACR, loads secrets from the shared
// Key Vault (SologicalSms--* names), talks to the shared PSQL server (own `sologicalsms`
// database only). Model: Billing's ops/infra/container-apps.bicep.
// Deploy: az deployment group create -g rg-aiworkforce-dev -f ops/infra/container-apps.bicep -p ops/infra/container-apps.bicepparam

param location string

@description('Existing Container Apps environment ID (shared with AI-Workforce)')
param environmentId string

@description('Existing ACR login server (shared with AI-Workforce)')
param containerRegistryLoginServer string

@description('Existing managed identity resource ID (shared with AI-Workforce)')
param managedIdentityId string

@description('Key Vault URI for secret loading')
param keyVaultUri string

@description('Client ID of the managed identity (for DefaultAzureCredential)')
param managedIdentityClientId string

@description('Image tag to deploy (e.g., git SHA or "latest")')
param imageTag string = 'latest'

// ── SMS service Container App (external — SMS Central webhook forwards + AIW sends) ──

resource smsService 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-sologicalsms'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: containerRegistryLoginServer
          identity: managedIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'sologicalsms'
          image: '${containerRegistryLoginServer}/sologicalsms:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Staging' }
            { name: 'ASPNETCORE_URLS', value: 'http://0.0.0.0:8080' }
            { name: 'AZURE_CLIENT_ID', value: managedIdentityClientId }
            { name: 'AzureKeyVault__VaultUri', value: keyVaultUri }
            // Strict SLVERIFY on the SMS Central ingress routes, same as local dev.
            { name: 'SologicalSms__Ingress__RequireVerification', value: 'true' }
            // Breaker trip -> one-shot operator alert SMS (public-surface slice).
            { name: 'SologicalSms__Breaker__AlertNumber', value: '+61408004199' }
            // Secrets arrive via Key Vault (SologicalSms--ConnectionStrings--DefaultConnection,
            // --SmsCentral--User/--Password, --Ingress--VerifyKey, --Webhook--AIWorkforce).
          ]
          probes: [
            // Startup window covers migrate-on-first-boot against the shared PSQL server.
            { type: 'Startup',   httpGet: { path: '/health', port: 8080 }, periodSeconds: 5,  failureThreshold: 12 }
            { type: 'Liveness',  httpGet: { path: '/health', port: 8080 }, periodSeconds: 30, failureThreshold: 3  }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ── Outputs ─────────────────────────────────────────────────────────────────

output smsServiceFqdn string = smsService.properties.configuration.ingress.fqdn
