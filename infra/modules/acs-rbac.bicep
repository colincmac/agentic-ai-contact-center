targetScope = 'resourceGroup'

@description('Name of the shared Azure Communication Services account.')
param communicationServicesAccountName string

@description('Principal ID of the regional AKS workload identity.')
param workloadPrincipalId string

resource communicationServices 'Microsoft.Communication/communicationServices@2026-03-18' existing = {
  name: communicationServicesAccountName
}

resource workloadCommunicationDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: communicationServices
  name: guid(communicationServices.id, workloadPrincipalId, 'Azure Communication Services Data Owner')
  properties: {
    principalId: workloadPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  }
}
