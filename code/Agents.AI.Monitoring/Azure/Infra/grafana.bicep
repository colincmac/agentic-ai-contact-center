// Azure Managed Grafana — the contact-center observability single pane.
//
// Provisions the Grafana instance with a system-assigned identity, integrates the Managed
// Prometheus (Azure Monitor workspace) that backs the ${prom}-sourced panels, and grants
// Grafana's identity Monitoring Reader over the target scope so its built-in Azure Monitor
// data source can read Log Analytics, Application Insights, and metrics.
//
// This codifies the manual runbook in docs/monitoring/setup.md (section 4). Dashboards are
// imported separately from Dashboards/generated/*.json (Grafana has no ARM surface for
// dashboard content); see setup.md for the `az grafana dashboard create` / provisioning step.

@description('Name of the Azure Managed Grafana instance.')
param name string

@description('Location for the Grafana instance.')
param location string = resourceGroup().location

@description('Resource id of the Azure Monitor workspace (Managed Prometheus) backing the metric panels. Leave empty to skip the integration.')
param azureMonitorWorkspaceResourceId string = ''

@description('Object id (principal id) of a user or group to grant the Grafana Admin RBAC role. Leave empty to skip.')
param grafanaAdminPrincipalId string = ''

@description('Grafana major version.')
param grafanaMajorVersion string = '11'

@description('Tags applied to the Grafana instance.')
param tags object = {}

// Built-in role definition ids.
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
var grafanaAdminRoleId = '22926164-76b3-42b3-bc55-97df8dab3e41'

resource grafana 'Microsoft.Dashboard/grafana@2023-09-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    grafanaMajorVersion: grafanaMajorVersion
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
    apiKey: 'Disabled'
    deterministicOutboundIP: 'Disabled'
    grafanaIntegrations: {
      azureMonitorWorkspaceIntegrations: empty(azureMonitorWorkspaceResourceId) ? [] : [
        {
          azureMonitorWorkspaceResourceId: azureMonitorWorkspaceResourceId
        }
      ]
    }
  }
}

// Let Grafana READ Azure Monitor (Log Analytics, App Insights, metrics) across this resource group.
resource monitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, grafana.id, monitoringReaderRoleId)
  scope: resourceGroup()
  properties: {
    principalId: grafana.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReaderRoleId)
  }
}

// Give an operator user/group access to Grafana itself.
resource grafanaAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(grafanaAdminPrincipalId)) {
  name: guid(grafana.id, grafanaAdminPrincipalId, grafanaAdminRoleId)
  scope: grafana
  properties: {
    principalId: grafanaAdminPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', grafanaAdminRoleId)
  }
}

@description('Endpoint of the provisioned Grafana instance.')
output endpoint string = grafana.properties.endpoint

@description('Principal id of the Grafana system-assigned identity.')
output principalId string = grafana.identity.principalId

@description('Resource id of the Grafana instance.')
output grafanaResourceId string = grafana.id
