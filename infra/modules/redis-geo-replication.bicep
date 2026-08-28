targetScope = 'resourceGroup'

@description('Azure region for this redis stamp.')
param location string = resourceGroup().location

@description('Redis cluster name.')
param redisClusterName string

@description('Azure Managed Redis SKU.')
param redisSkuName string = 'Balanced_B10'

@description('Resource IDs of every database in the active geo-replication group.')
param linkedDatabaseIds string[] 

@description('Stable nickname for the active geo-replication group.')
param groupNickname string

@description('Workload identity for accessing the Redis database.')
param workloadIdentityPrincipalId string

param privateEndpointSubnetId string
param privateDnsZoneId string
  
@description('Tags applied to regional resources.')
param tags object = {}



resource redisCluster 'Microsoft.Cache/redisEnterprise@2026-06-01-preview' = {
  name: redisClusterName
  location: location
  tags: tags
  sku: {
    name: redisSkuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    encryption: {}
    highAvailability: 'Enabled'
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }

  resource redisDatabase 'databases@2025-07-01' = {
    name: 'default'
    properties: {
      clusteringPolicy: 'OSSCluster'
      geoReplication: {
        groupNickname: groupNickname
        linkedDatabases: [for dbId in linkedDatabaseIds: {
          id: dbId
        }]
      }
    }
  }
}

resource workloadRedisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  parent: redisCluster::redisDatabase
  name: take(replace(guid(redisCluster::redisDatabase.id, workloadIdentityPrincipalId), '-', ''), 60)
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: workloadIdentityPrincipalId
    }
  }
}

module redisPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'redis-private-endpoint'
  params: {
    groupIds: [
      'redisEnterprise'
    ]
    location: location
    name: 'pep-${redisCluster.name}'
    privateDnsZoneIds: [
      privateDnsZoneId
    ]
    privateLinkServiceId: redisCluster.id
    subnetId: privateEndpointSubnetId
    tags: tags
  }
}


output redisClusterId string = redisCluster.id
output redisClusterName string = redisCluster.name
output redisDatabaseId string = redisCluster::redisDatabase.id
