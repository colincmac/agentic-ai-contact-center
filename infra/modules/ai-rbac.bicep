targetScope = 'resourceGroup'

@description('Name of the regional Microsoft Foundry AI Services account.')
param aiServicesAccountName string

@description('Principal ID of the regional AKS workload identity.')
param workloadPrincipalId string

resource aiServicesAccount 'Microsoft.CognitiveServices/accounts@2026-05-01' existing = {
  name: aiServicesAccountName
}

resource workloadAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: aiServicesAccount
  name: guid(aiServicesAccount.id, workloadPrincipalId, 'Cognitive Services User')
  properties: {
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a97b65f3-24c7-4388-baec-2e87135dc908'
    )
  }
}
