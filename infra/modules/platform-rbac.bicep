targetScope = 'resourceGroup'

@description('Name of the platform ACR.')
param containerRegistryName string

@description('Name of the shared Application Insights component.')
param applicationInsightsName string

@description('Name of the regional AKS private DNS zone.')
param aksPrivateDnsZoneName string

@description('Principal ID of the AKS control-plane identity.')
param controlPlanePrincipalId string

@description('Principal ID of the AKS kubelet identity.')
param kubeletPrincipalId string

@description('Principal ID of the Foundry project identity.')
param foundryProjectPrincipalId string

@description('Assign the shared Application Insights reader role to the Foundry project in this module instance.')
param assignFoundryProjectLogReader bool = false

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: containerRegistryName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource aksPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: aksPrivateDnsZoneName
}

resource kubeletAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry
  name: guid(containerRegistry.id, kubeletPrincipalId, 'AcrPull')
  properties: {
    principalId: kubeletPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource controlPlanePrivateDnsContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: aksPrivateDnsZone
  name: guid(aksPrivateDnsZone.id, controlPlanePrincipalId, 'Private DNS Zone Contributor')
  properties: {
    principalId: controlPlanePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b12aa53e-6015-4669-85d0-8515ebb3ae7f')
  }
}

resource foundryProjectLogReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignFoundryProjectLogReader && !empty(foundryProjectPrincipalId)) {
  scope: applicationInsights
  name: guid(applicationInsights.id, foundryProjectPrincipalId, 'Log Analytics Reader')
  properties: {
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')
  }
}
