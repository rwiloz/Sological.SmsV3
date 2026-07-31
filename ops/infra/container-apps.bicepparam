using './container-apps.bicep'

// Sological SMS v3 — deploys into the shared AI-Workforce dev environment.
// Deploy: az deployment group create -g rg-aiworkforce-dev -f ops/infra/container-apps.bicep -p ops/infra/container-apps.bicepparam

param location = 'australiaeast'
param imageTag = 'latest'

// References to existing shared AI-Workforce infrastructure
param environmentId = '/subscriptions/0f2cd63f-c63e-4046-b75f-bdd44d4dfbfb/resourceGroups/rg-aiworkforce-dev/providers/Microsoft.App/managedEnvironments/cae-aiworkforce-dev'
param containerRegistryLoginServer = 'craiworkforcedev.azurecr.io'
param managedIdentityId = '/subscriptions/0f2cd63f-c63e-4046-b75f-bdd44d4dfbfb/resourceGroups/rg-aiworkforce-dev/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-aiworkforce-dev'
param managedIdentityClientId = '9488f698-1d5c-445d-9763-52c28a17108c'
param keyVaultUri = 'https://kv-aiworkforce-dev.vault.azure.net/'
