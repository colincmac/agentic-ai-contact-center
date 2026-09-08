targetScope = 'resourceGroup'

@description('Name of the private endpoint.')
param name string

@description('Azure region for the private endpoint.')
param location string = resourceGroup().location

@description('Resource ID of the subnet that hosts the private endpoint.')
param subnetId string

@description('Resource ID of the Private Link service.')
param privateLinkServiceId string

@description('Private Link group IDs exposed by the target service.')
param groupIds string[]

@description('Private DNS zone resource IDs associated with the endpoint.')
param privateDnsZoneIds string[]

@description('Tags applied to the private endpoint.')
param tags object = {}

resource privateEndpoint 'Microsoft.Network/privateEndpoints@2025-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    customNetworkInterfaceName: 'nic-${name}'
    privateLinkServiceConnections: [
      {
        name: 'connection'
        properties: {
          groupIds: groupIds
          privateLinkServiceId: privateLinkServiceId
        }
      }
    ]
    subnet: {
      id: subnetId
    }
  }
}

resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-07-01' = if (!empty(privateDnsZoneIds)) {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      for (privateDnsZoneId, index) in privateDnsZoneIds: {
        name: 'zone-${index}'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output id string = privateEndpoint.id
