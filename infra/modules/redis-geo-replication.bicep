targetScope = 'resourceGroup'

@description('Name of the Azure Managed Redis cluster in this stamp.')
param redisClusterName string

@description('Resource IDs of every database in the active geo-replication group.')
param linkedDatabaseIds string[]

@description('Stable nickname for the active geo-replication group.')
param groupNickname string

resource redisCluster 'Microsoft.Cache/redisEnterprise@2026-06-01-preview' existing = {
  name: redisClusterName
}

var redisDatabaseId = '${redisCluster.id}/databases/default'
var peerDatabaseIds = filter(linkedDatabaseIds, linkedDatabaseId => linkedDatabaseId != redisDatabaseId)

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2026-06-01-preview' = {
  parent: redisCluster
  name: 'default'
  properties: {
    accessKeysAuthentication: 'Disabled'
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'NoEviction'
    geoReplication: {
      groupNickname: groupNickname
      linkedDatabases: [
        for linkedDatabaseId in peerDatabaseIds: {
          id: linkedDatabaseId
        }
      ]
    }
  }
}

output databaseId string = redisDatabaseId
