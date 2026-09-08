targetScope = 'resourceGroup'

@description('Primary Azure region for global application resources.')
param location string = resourceGroup().location

@description('AZD environment name used for deterministic resource names.')
param environmentName string

var normalizedEnvironmentName = toLower(environmentName)

@description('Name of the shared Azure Bot resource.')
param botServiceName string

@description('Bot messaging endpoint. Leave empty until the application ingress is deployed.')
param botMessagingEndpoint string = ''

@description('Tags applied to global application resources.')
param tags object = {}


// For scenarios where Teams and the Azure resources are in different tenants, this needs to be an Entra App Registration instead
resource botIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('id-bot-zava-${normalizedEnvironmentName}', 128)
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

output botServiceId string = botService.id
output botServiceName string = botService.name
output botIdentityClientId string = botIdentity.properties.clientId
