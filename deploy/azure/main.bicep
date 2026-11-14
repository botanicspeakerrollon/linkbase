// The Linkbase demo on Azure: Container Apps, Postgres, a registry and a Key Vault. See README.md in this folder.
//
// Deployed twice by deploy.ps1, because the app can't start before its image and secrets exist:
//   1. deployApp=false  everything the app needs: vault, registry, database, identity
//   2. deployApp=true   the Container App, once the image is built and the secrets are in the vault
// Secrets never pass through this file. deploy.ps1 writes them into the vault, and the app reads them with its managed
// identity at start-up.

targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Lower-case letters and digits. A hash of the resource group is added, so names are unique.')
@maxLength(10)
param prefix string = 'linkbase'

@description('Who deploy.ps1 is signed in as. Gets Key Vault Secrets Officer, so the script can write the secrets.')
param deployerObjectId string

@secure()
@description('The Postgres administrator password. deploy.ps1 generates it and keeps it in the vault.')
param postgresPassword string

@description('Addresses allowed to reach the demo, as CIDR ranges such as 203.0.113.7/32. Empty means anyone, which is only right when something else signs people in.')
param allowedCidrs array = []

@description('Builds the Container App. False for the first pass, before the image and secrets exist.')
param deployApp bool = false

@description('The image tag in the registry.')
param imageTag string = 'latest'

@description('Whether Read text and Ask can run. They need the Anthropic key in the vault.')
param extractionEnabled bool = true

var suffix = uniqueString(resourceGroup().id)
var baseName = '${prefix}${take(suffix, 6)}'
var postgresAdmin = 'linkbase'
var databases = [
  { name: 'climbing_demo', setting: 'ClimbingDemo', secret: 'cs-climbing-demo' }
  { name: 'consultancy', setting: 'Consultancy', secret: 'cs-consultancy' }
  { name: 'linkbase_code', setting: 'LinkbaseCode', secret: 'cs-linkbase-code' }
  { name: 'archimate', setting: 'ArchiMate', secret: 'cs-archimate' }
]

// Role definition ids are the same in every tenant.
var roleKeyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var roleKeyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var roleAcrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-id'
  location: location
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${baseName}-kv'
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource deployerCanWriteSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, deployerObjectId, roleKeyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleKeyVaultSecretsOfficer)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

resource appCanReadSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, roleKeyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleKeyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${baseName}acr'
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource appCanPullImage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, roleAcrPull)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleAcrPull)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01-preview' = {
  name: '${baseName}-pg'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: postgresAdmin
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

// The migrations create pg_trgm, and Azure refuses extensions it hasn't been told to allow.
resource trigram 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2023-12-01-preview' = {
  parent: postgres
  name: 'azure.extensions'
  properties: { value: 'PG_TRGM', source: 'user-override' }
}

// 0.0.0.0 is Azure's way of saying "other Azure services": the Container App, not the internet. Connections need the
// password and TLS either way.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-12-01-preview' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
  dependsOn: [ trigram ]
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${baseName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var secretVaultUri = 'https://${vault.name}${az.environment().suffixes.keyvaultDns}/'
var databaseSecrets = [for db in databases: {
  name: db.secret
  keyVaultUrl: '${secretVaultUri}secrets/${db.secret}'
  identity: identity.id
}]
var appSecrets = concat(
  databaseSecrets,
  extractionEnabled ? [
    {
      name: 'anthropic-api-key'
      keyVaultUrl: '${secretVaultUri}secrets/anthropic-api-key'
      identity: identity.id
    }
  ] : []
)

var databaseEnv = [for db in databases: {
  name: 'ConnectionStrings__${db.setting}'
  secretRef: db.secret
}]
var appEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Demo' }
    { name: 'Extraction__Enabled', value: string(extractionEnabled) }
  ],
  databaseEnv,
  extractionEnabled ? [
    { name: 'Anthropic__ApiKey', secretRef: 'anthropic-api-key' }
  ] : []
)

var ipRules = [for (cidr, i) in allowedCidrs: {
  name: 'allow-${i}'
  action: 'Allow'
  ipAddressRange: cidr
}]

resource app 'Microsoft.App/containerApps@2024-03-01' = if (deployApp) {
  // Just the prefix, not baseName: the name is the first label of the public URL, and the hash in baseName is random
  // letters that can spell something unfortunate. A Container App's name only has to be unique within its environment.
  name: prefix
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: appEnvironment.id
    configuration: {
      // Blazor Server holds each browser's session in one process, so a single replica needs no sticky sessions.
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        ipSecurityRestrictions: ipRules
      }
      registries: [
        { server: registry.properties.loginServer, identity: identity.id }
      ]
      secrets: appSecrets
    }
    template: {
      // Creates each model's database, applies the migrations and loads the example data. It does nothing to a model that
      // already has data, so every restart can run it.
      initContainers: [
        {
          name: 'setup'
          image: '${registry.properties.loginServer}/linkbase:${imageTag}'
          args: [ 'model', 'setup' ]
          env: appEnv
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
      containers: [
        {
          name: 'linkbase'
          image: '${registry.properties.loginServer}/linkbase:${imageTag}'
          env: appEnv
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 5
              failureThreshold: 36
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 20
            }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [ appCanReadSecrets, appCanPullImage, allowAzure ]
}

output vaultName string = vault.name
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output postgresHost string = postgres.properties.fullyQualifiedDomainName
output postgresAdmin string = postgresAdmin
output appUrl string = deployApp ? 'https://${app!.properties.configuration.ingress.fqdn}' : ''
