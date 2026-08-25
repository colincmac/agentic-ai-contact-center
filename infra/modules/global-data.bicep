targetScope = 'resourceGroup'

@description('Primary Azure region for global application resources.')
param location string = resourceGroup().location

@description('All regional stamp locations, including the primary region.')
param stampLocations string[]

@description('Stable token used for globally unique resource names.')
param resourceToken string

@description('Name of the shared Cosmos DB account.')
param cosmosAccountName string

@description('Name of the shared Azure Bot resource.')
param botServiceName string

@description('Bot messaging endpoint. Leave empty until the application ingress is deployed.')
param botMessagingEndpoint string = ''

@description('Resource ID of the shared Log Analytics workspace.')
param logAnalyticsWorkspaceId string

@description('Tags applied to global application resources.')
param tags object = {}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' = {
  name: cosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    databaseAccountOfferType: 'Standard'
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuth: true
    enableAnalyticalStorage: false
    enableAutomaticFailover: true
    enableFreeTier: false
    enableMultipleWriteLocations: length(stampLocations) > 1
    locations: [
      for (stampLocation, index) in stampLocations: {
        failoverPriority: index
        isZoneRedundant: false
        locationName: stampLocation
      }
    ]
    minimalTlsVersion: 'Tls12'
    networkAclBypass: 'None'
    networkAclBypassResourceIds: []
    publicNetworkAccess: 'Disabled'
    virtualNetworkRules: []
  }
}

resource cosmosDiagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-shared-log-analytics'
  scope: cosmosAccount
  properties: {
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
    workspaceId: logAnalyticsWorkspaceId
  }
}

resource botIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-bot-zava-${take(resourceToken, 8)}'
  location: location
  tags: tags
}

resource botService 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botServiceName
  location: 'global'
  tags: tags
  kind: 'azurebot'
  sku: {
    name: 'S1'
  }
  properties: {
    displayName: 'Zava Financial Contact Center'
    endpoint: botMessagingEndpoint
    msaAppId: botIdentity.properties.clientId
    msaAppMSIResourceId: botIdentity.id
    msaAppTenantId: tenant().tenantId
    msaAppType: 'UserAssignedMSI'
    publicNetworkAccess: 'Enabled'
  }
}

output cosmosAccountId string = cosmosAccount.id
output cosmosAccountName string = cosmosAccount.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output botServiceId string = botService.id
output botServiceName string = botService.name
output botIdentityClientId string = botIdentity.properties.clientId
