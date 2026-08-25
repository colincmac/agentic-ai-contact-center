targetScope = 'resourceGroup'

@description('Name of the AKS cluster that emits managed Prometheus metrics.')
param clusterName string

@description('Azure region of the AKS cluster.')
param clusterLocation string

@description('Resource ID of the shared Azure Monitor workspace.')
param azureMonitorWorkspaceId string

@description('Azure region of the shared Azure Monitor workspace.')
param azureMonitorWorkspaceLocation string

@description('Tags applied to the Prometheus data collection resources.')
param tags object = {}

var prometheusResourceName = 'MSProm-${azureMonitorWorkspaceLocation}-${clusterName}'
var dataCollectionEndpointName = take(prometheusResourceName, 44)
var dataCollectionRuleName = take(prometheusResourceName, 64)

resource aksCluster 'Microsoft.ContainerService/managedClusters@2026-05-01' existing = {
  name: clusterName
}

resource prometheusDataCollectionEndpoint 'Microsoft.Insights/dataCollectionEndpoints@2024-03-11' = {
  name: dataCollectionEndpointName
  location: azureMonitorWorkspaceLocation
  kind: 'Linux'
  tags: tags
  properties: {
    description: 'Data collection endpoint for ${clusterName} managed Prometheus metrics.'
    networkAcls: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

// This API version is the current stable Prometheus DCR API used by Microsoft's collector templates.
#disable-next-line BCP081
resource prometheusDataCollectionRule 'Microsoft.Insights/dataCollectionRules@2025-05-11' = {
  name: dataCollectionRuleName
  location: azureMonitorWorkspaceLocation
  kind: 'Linux'
  tags: tags
  properties: {
    dataCollectionEndpointId: prometheusDataCollectionEndpoint.id
    dataFlows: [
      {
        destinations: [
          'MonitoringAccount'
        ]
        streams: [
          'Microsoft-PrometheusMetrics'
        ]
      }
    ]
    dataSources: {
      prometheusForwarder: [
        {
          labelIncludeFilter: {}
          name: 'PrometheusDataSource'
          streams: [
            'Microsoft-PrometheusMetrics'
          ]
        }
      ]
    }
    description: 'Routes ${clusterName} Prometheus metrics to the shared Zava Azure Monitor workspace.'
    destinations: {
      monitoringAccounts: [
        {
          accountResourceId: azureMonitorWorkspaceId
          name: 'MonitoringAccount'
        }
      ]
    }
  }
}

resource prometheusDataCollectionAssociation 'Microsoft.Insights/dataCollectionRuleAssociations@2024-03-11' = {
  name: 'MSProm-${clusterLocation}-${clusterName}'
  scope: aksCluster
  properties: {
    dataCollectionRuleId: prometheusDataCollectionRule.id
    description: 'Managed Prometheus collection for ${clusterName}.'
  }
}

output dataCollectionEndpointId string = prometheusDataCollectionEndpoint.id
output dataCollectionRuleId string = prometheusDataCollectionRule.id
