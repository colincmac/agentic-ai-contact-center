targetScope = 'resourceGroup'

@description('Names of the centralized private DNS zones.')
param privateDnsZoneNames string[]

@description('Resource ID of the VNet linked to every private DNS zone.')
param virtualNetworkId string

@description('Short token used to create stable link names.')
param linkToken string

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' existing = [
  for privateDnsZoneName in privateDnsZoneNames: {
    name: privateDnsZoneName
  }
]

resource virtualNetworkLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (privateDnsZoneName, index) in privateDnsZoneNames: {
    parent: privateDnsZones[index]
    name: 'link-${linkToken}-${take(uniqueString(privateDnsZoneName, virtualNetworkId), 6)}'
    location: 'global'
    properties: {
      registrationEnabled: false
      resolutionPolicy: 'NxDomainRedirect'
      virtualNetwork: {
        id: virtualNetworkId
      }
    }
  }
]

output linkIds string[] = [
  for (privateDnsZoneName, index) in privateDnsZoneNames: virtualNetworkLinks[index].id
]
