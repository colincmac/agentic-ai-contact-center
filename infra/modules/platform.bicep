targetScope = 'resourceGroup'

@description('Primary Azure region for the platform landing-zone resources.')
param location string = resourceGroup().location

@description('All regional stamp locations, including the primary region.')
param stampLocations string[]

@description('Name of the hub virtual network.')
param hubVirtualNetworkName string

@description('Address space assigned to the platform hub VNet.')
param hubAddressPrefix string = '10.0.0.0/16'

@description('Name of the shared Log Analytics workspace.')
param logAnalyticsWorkspaceName string

@description('Name of the shared Application Insights component.')
param applicationInsightsName string

@description('Name of the Premium container registry to create when no existing registry is supplied.')
param containerRegistryName string

@description('Optional existing container registry resource ID.')
param existingContainerRegistryResourceId string = ''

@description('Optional existing container registry login server.')
param existingContainerRegistryEndpoint string = ''

@description('Name of the API Management service.')
param apiManagementName string

@description('API Management publisher name.')
param apiManagementPublisherName string = 'Zava Financial'

@description('API Management publisher email.')
param apiManagementPublisherEmail string = 'platform@zavafinancial.example'

@description('Deploy Azure Bastion in the hub VNet.')
param deployBastion bool = true

@description('Principal ID that can push images and run ACR tasks.')
param principalId string

@description('Principal type for the deployment principal.')
param principalType string

@description('Tags applied to platform resources.')
param tags object = {}

var createContainerRegistry = empty(existingContainerRegistryResourceId)
var staticPrivateDnsZoneNames = [
  'privatelink.azurecr.io'
  'privatelink.services.ai.azure.com'
  'privatelink.openai.azure.com'
  'privatelink.cognitiveservices.azure.com'
  'privatelink.search.windows.net'
  'privatelink.vaultcore.azure.net'
  'privatelink.blob.core.windows.net'
  'privatelink.file.core.windows.net'
  'privatelink.queue.core.windows.net'
  'privatelink.table.core.windows.net'
  'privatelink.documents.azure.com'
  'privatelink.redisenterprise.cache.azure.net'
  'privatelink.azconfig.io'
  'privatelink.azure-api.net'
]
var aksPrivateDnsZoneNames = [for stampLocation in stampLocations: 'privatelink.${stampLocation}.azmk8s.io']
var privateDnsZoneNames = union(staticPrivateDnsZoneNames, aksPrivateDnsZoneNames)

resource apimNetworkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2025-07-01' = {
  name: 'nsg-apim-integration-${location}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowStorageHttpsOutbound'
        properties: {
          access: 'Allow'
          destinationAddressPrefix: 'Storage'
          destinationPortRange: '443'
          direction: 'Outbound'
          priority: 100
          protocol: 'Tcp'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
        }
      }
      {
        name: 'AllowKeyVaultHttpsOutbound'
        properties: {
          access: 'Allow'
          destinationAddressPrefix: 'AzureKeyVault'
          destinationPortRange: '443'
          direction: 'Outbound'
          priority: 110
          protocol: 'Tcp'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
        }
      }
      {
        name: 'AllowVirtualNetworkOutbound'
        properties: {
          access: 'Allow'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange: '*'
          direction: 'Outbound'
          priority: 120
          protocol: '*'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
        }
      }
    ]
  }
}

resource hubVirtualNetwork 'Microsoft.Network/virtualNetworks@2025-07-01' = {
  name: hubVirtualNetworkName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        hubAddressPrefix
      ]
    }
    enableDdosProtection: false
  }
}

resource bastionSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = if (deployBastion) {
  parent: hubVirtualNetwork
  name: 'AzureBastionSubnet'
  properties: {
    addressPrefix: cidrSubnet(hubAddressPrefix, 26, 0)
    privateEndpointNetworkPolicies: 'Enabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = {
  parent: hubVirtualNetwork
  name: 'snet-private-endpoints'
  properties: {
    addressPrefix: cidrSubnet(hubAddressPrefix, 24, 1)
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
  dependsOn: [
    bastionSubnet
  ]
}

resource apiManagementIntegrationSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = {
  parent: hubVirtualNetwork
  name: 'snet-apim-integration'
  properties: {
    addressPrefix: cidrSubnet(hubAddressPrefix, 27, 16)
    delegations: [
      {
        name: 'Microsoft.Web.serverFarms'
        properties: {
          serviceName: 'Microsoft.Web/serverFarms'
        }
      }
    ]
    networkSecurityGroup: {
      id: apimNetworkSecurityGroup.id
    }
    privateEndpointNetworkPolicies: 'Enabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
  dependsOn: [
    privateEndpointSubnet
  ]
}

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for privateDnsZoneName in privateDnsZoneNames: {
    name: privateDnsZoneName
    location: 'global'
    tags: tags
    properties: {}
  }
]

resource hubPrivateDnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (privateDnsZoneName, index) in privateDnsZoneNames: {
    parent: privateDnsZones[index]
    name: 'link-hub-${take(uniqueString(hubVirtualNetwork.id, privateDnsZoneName), 8)}'
    location: 'global'
    properties: {
      registrationEnabled: false
      resolutionPolicy: 'NxDomainRedirect'
      virtualNetwork: {
        id: hubVirtualNetwork.id
      }
    }
  }
]

module logAnalytics '../core/monitor/loganalytics.bicep' = {
  name: 'shared-log-analytics'
  params: {
    location: location
    name: logAnalyticsWorkspaceName
    tags: tags
  }
}

module applicationInsights '../core/monitor/applicationinsights.bicep' = {
  name: 'shared-application-insights'
  params: {
    location: location
    name: applicationInsightsName
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    tags: tags
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2025-11-01' = if (createContainerRegistry) {
  name: containerRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Premium'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    dataEndpointEnabled: true
    networkRuleBypassOptions: 'AzureServices'
    publicNetworkAccess: 'Disabled'
    zoneRedundancy: 'Enabled'
  }
}

resource containerRegistryReplications 'Microsoft.ContainerRegistry/registries/replications@2025-11-01' = [
  for replicaLocation in filter(stampLocations, stampLocation => stampLocation != location): if (createContainerRegistry) {
    parent: containerRegistry
    name: replicaLocation
    location: replicaLocation
    tags: tags
    properties: {
      regionEndpointEnabled: true
      zoneRedundancy: 'Enabled'
    }
  }
]

resource deploymentPrincipalAcrPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createContainerRegistry) {
  scope: containerRegistry
  name: guid(containerRegistry.id, principalId, 'AcrPush')
  properties: {
    principalId: principalId
    principalType: principalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8311e382-0749-4cb8-b61a-304f252e45ec')
  }
}

var resolvedContainerRegistryId = createContainerRegistry ? containerRegistry!.id : existingContainerRegistryResourceId
var resolvedContainerRegistryName = createContainerRegistry ? containerRegistry!.name : last(split(existingContainerRegistryResourceId, '/'))
var resolvedContainerRegistryEndpoint = createContainerRegistry ? containerRegistry!.properties.loginServer : existingContainerRegistryEndpoint

module containerRegistryPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'acr-private-endpoint'
  params: {
    groupIds: [
      'registry'
    ]
    location: location
    name: 'pep-${resolvedContainerRegistryName}'
    privateDnsZoneIds: [
      privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.azurecr.io')].id
    ]
    privateLinkServiceId: resolvedContainerRegistryId
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
}

resource apiManagement 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apiManagementName
  location: location
  tags: tags
  sku: {
    name: 'StandardV2'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: apiManagementPublisherEmail
    publisherName: apiManagementPublisherName
    publicNetworkAccess: 'Enabled'
    virtualNetworkConfiguration: {
      subnetResourceId: apiManagementIntegrationSubnet.id
    }
    virtualNetworkType: 'External'
  }
}

resource bastionPublicIp 'Microsoft.Network/publicIPAddresses@2025-07-01' = if (deployBastion) {
  name: 'pip-bas-zava-${location}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource bastion 'Microsoft.Network/bastionHosts@2025-07-01' = if (deployBastion) {
  name: 'bas-zava-platform-${location}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    disableCopyPaste: false
    enableFileCopy: true
    enableIpConnect: true
    enableKerberos: true
    enableShareableLink: false
    enableTunneling: true
    ipConfigurations: [
      {
        name: 'bastion-ip-configuration'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: {
            id: bastionPublicIp!.id
          }
          subnet: {
            id: bastionSubnet!.id
          }
        }
      }
    ]
    scaleUnits: 2
  }
}

output hubVirtualNetworkId string = hubVirtualNetwork.id
output hubVirtualNetworkName string = hubVirtualNetwork.name
output privateEndpointSubnetId string = privateEndpointSubnet.id
output privateDnsZoneNames string[] = privateDnsZoneNames
output privateDnsZoneIds object = {
  acr: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.azurecr.io')].id
  aiServices: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.services.ai.azure.com')].id
  openAi: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.openai.azure.com')].id
  cognitiveServices: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.cognitiveservices.azure.com')].id
  keyVault: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.vaultcore.azure.net')].id
  storageBlob: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.blob.core.windows.net')].id
  storageFile: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.file.core.windows.net')].id
  storageQueue: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.queue.core.windows.net')].id
  storageTable: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.table.core.windows.net')].id
  cosmosSql: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.documents.azure.com')].id
  redis: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.redisenterprise.cache.azure.net')].id
  appConfiguration: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.azconfig.io')].id
  apiManagement: privateDnsZones[indexOf(privateDnsZoneNames, 'privatelink.azure-api.net')].id
}
output aksPrivateDnsZoneIds string[] = [
  for aksPrivateDnsZoneName in aksPrivateDnsZoneNames: privateDnsZones[indexOf(privateDnsZoneNames, aksPrivateDnsZoneName)].id
]
output logAnalyticsWorkspaceId string = logAnalytics.outputs.id
output logAnalyticsWorkspaceName string = logAnalytics.outputs.name
output applicationInsightsId string = applicationInsights.outputs.id
output applicationInsightsName string = applicationInsights.outputs.name
output applicationInsightsConnectionString string = applicationInsights.outputs.connectionString
output containerRegistryId string = resolvedContainerRegistryId
output containerRegistryName string = resolvedContainerRegistryName
output containerRegistryEndpoint string = resolvedContainerRegistryEndpoint
output apiManagementId string = apiManagement.id
output apiManagementName string = apiManagement.name
output apiManagementGatewayUrl string = apiManagement.properties.gatewayUrl
