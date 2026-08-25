targetScope = 'resourceGroup'

@description('Name of the platform hub VNet.')
param hubVirtualNetworkName string

@description('Resource ID of the regional spoke VNet.')
param spokeVirtualNetworkId string

@description('Token used to identify the remote spoke.')
param spokeToken string

resource hubVirtualNetwork 'Microsoft.Network/virtualNetworks@2025-07-01' existing = {
  name: hubVirtualNetworkName
}

resource hubToSpokePeering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2025-07-01' = {
  parent: hubVirtualNetwork
  name: 'peer-to-${spokeToken}'
  properties: {
    allowForwardedTraffic: true
    allowGatewayTransit: false
    allowVirtualNetworkAccess: true
    remoteVirtualNetwork: {
      id: spokeVirtualNetworkId
    }
    useRemoteGateways: false
  }
}

output id string = hubToSpokePeering.id
