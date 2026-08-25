targetScope = 'resourceGroup'

@description('Name of the shared Cosmos DB account.')
param cosmosAccountName string

@description('Principal ID of the regional AKS workload identity.')
param workloadPrincipalId string

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' existing = {
  name: cosmosAccountName
}

resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, workloadPrincipalId, 'Cosmos DB Built-in Data Contributor')
  properties: {
    principalId: workloadPrincipalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: cosmosAccount.id
  }
}
