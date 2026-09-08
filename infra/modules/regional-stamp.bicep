targetScope = 'resourceGroup'

type identityReference = {
  id: string
  name: string
  clientId: string
  principalId: string
}

type privateDnsZoneReferences = {
  keyVault: string
  storageBlob: string
  storageQueue: string
  cosmosSql: string
  redis: string
  appConfiguration: string
}

@description('Azure region for this application landing-zone stamp.')
param location string = resourceGroup().location

@description('Zero-based index of this stamp in the regional stamp array.')
param regionIndex int

@description('Short, stable token used in regional resource names.')
param namingToken string

@description('Resource group that contains the shared platform resources.')
param platformResourceGroupName string

@description('Name of the shared hub VNet.')
param hubVirtualNetworkName string

@description('Resource ID of the shared hub VNet.')
param hubVirtualNetworkId string

@description('Names of all centralized private DNS zones.')
param privateDnsZoneNames string[]

@description('Resource IDs of private DNS zones used by regional PaaS private endpoints.')
param privateDnsZoneIds privateDnsZoneReferences

@description('Resource ID of the custom AKS private DNS zone for this region.')
param aksPrivateDnsZoneId string

@description('Resource ID of the shared Log Analytics workspace.')
param logAnalyticsWorkspaceId string

@description('Resource ID of the shared Azure Monitor workspace for managed Prometheus.')
param azureMonitorWorkspaceId string

@description('Azure region of the shared Azure Monitor workspace.')
param azureMonitorWorkspaceLocation string

@description('Resource ID of the shared Cosmos DB account.')
param cosmosAccountId string

@description('AKS control-plane user-assigned identity.')
param controlPlaneIdentity identityReference

@description('AKS kubelet user-assigned identity.')
param kubeletIdentity identityReference

@description('AKS workload user-assigned identity.')
param workloadIdentity identityReference

@description('Regional spoke VNet address prefix.')
param spokeAddressPrefix string

@description('AKS node subnet address prefix.')
param aksSubnetPrefix string

@description('Private endpoint subnet address prefix.')
param privateEndpointSubnetPrefix string

@description('AKS service CIDR.')
param aksServiceCidr string

@description('AKS DNS service IP.')
param aksDnsServiceIp string

@description('AKS pod CIDR used by Azure CNI Overlay.')
param aksPodCidr string

@description('AKS Kubernetes version. Empty selects the regional default.')
param kubernetesVersion string = ''

@description('Availability zones used by node pools. Use an empty array in regions without availability zones.')
param availabilityZones string[] = []

@description('Object IDs of Microsoft Entra groups with AKS cluster administrator access.')
param aksAdminGroupObjectIds string[] = []

@description('VM size for the AKS system node pool.')
param systemNodeVmSize string = 'Standard_D4ds_v7'

@minValue(1)
@description('Minimum node count for the AKS system pool.')
param systemNodeMinCount int = 3

@minValue(1)
@description('Maximum node count for the AKS system pool.')
param systemNodeMaxCount int = 6

@description('VM size for the voice-edge node pool.')
param voiceNodeVmSize string = 'Standard_D16ds_v7'

@minValue(1)
@description('Minimum node count for the voice-edge pool.')
param voiceNodeMinCount int = 1

@minValue(1)
@description('Maximum node count for the voice-edge pool.')
param voiceNodeMaxCount int = 3

@description('Deploy the dedicated Istio ingress gateway node pool from ADR-0015.')
param deployIstioGatewayNodePool bool = true

@description('VM size for the dedicated Istio ingress gateway pool.')
param istioGatewayNodeVmSize string = 'Standard_D8ds_v7'

@minValue(1)
@description('Minimum node count for the Istio gateway pool.')
param istioGatewayNodeMinCount int = 3

@minValue(1)
@description('Maximum node count for the Istio gateway pool.')
param istioGatewayNodeMaxCount int = 12

@description('Deploy an optional GPU node pool for intent and biometric workloads.')
param deployGpuNodePool bool = false

@description('VM size for the optional GPU pool.')
param gpuNodeVmSize string = 'Standard_NC4as_T4_v3'

@minValue(0)
@description('Minimum node count for the optional GPU pool.')
param gpuNodeMinCount int = 0

@minValue(1)
@description('Maximum node count for the optional GPU pool.')
param gpuNodeMaxCount int = 3

@description('Storage replication SKU used by the regional application storage account.')
param storageSkuName string = 'Standard_ZRS'

@description('Kubernetes namespace federated to the regional workload identity.')
param workloadIdentityNamespace string = 'contact-center'

@description('Kubernetes service account federated to the regional workload identity.')
param workloadIdentityServiceAccount string = 'voice-edge'

@description('Enable Microsoft Defender for Containers on AKS.')
param enableDefender bool = false

@description('Tags applied to regional resources.')
param tags object = {}

var spokeVirtualNetworkName = 'vnet-contact-center-${namingToken}'
var aksName = 'aks-zava-contact-center-${namingToken}'
var keyVaultName = take('kv-zava-${take(namingToken, 6)}', 24)
var storageAccountName = take(replace('stzavacc${take(namingToken, 8)}', '-', ''), 24)
var appConfigurationName = take('appcs-zava-${take(namingToken, 6)}', 50)

resource controlPlaneUserAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: controlPlaneIdentity.name
}

resource kubeletUserAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: kubeletIdentity.name
}

resource workloadUserAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: workloadIdentity.name
}

resource aksNetworkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2025-07-01' = {
  name: 'nsg-aks-${location}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowAzureLoadBalancerInbound'
        properties: {
          access: 'Allow'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
          direction: 'Inbound'
          priority: 100
          protocol: '*'
          sourceAddressPrefix: 'AzureLoadBalancer'
          sourcePortRange: '*'
        }
      }
      {
        name: 'AllowVirtualNetworkInbound'
        properties: {
          access: 'Allow'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange: '*'
          direction: 'Inbound'
          priority: 110
          protocol: '*'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
        }
      }
    ]
  }
}

resource natGatewayPublicIp 'Microsoft.Network/publicIPAddresses@2025-07-01' = {
  name: 'pip-ng-zava-${location}'
  location: location
  tags: tags
  sku: {
    name: 'StandardV2'
    tier: 'Regional'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}

resource natGateway 'Microsoft.Network/natGateways@2025-07-01' = {
  name: 'ng-zava-contact-center-${location}'
  location: location
  tags: tags
  sku: {
    name: 'StandardV2'
  }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [
      {
        id: natGatewayPublicIp.id
      }
    ]
  }
}

resource spokeVirtualNetwork 'Microsoft.Network/virtualNetworks@2025-07-01' = {
  name: spokeVirtualNetworkName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        spokeAddressPrefix
      ]
    }
    enableDdosProtection: false
  }
}

resource aksSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = {
  parent: spokeVirtualNetwork
  name: 'snet-aks'
  properties: {
    addressPrefix: aksSubnetPrefix
    natGateway: {
      id: natGateway.id
    }
    networkSecurityGroup: {
      id: aksNetworkSecurityGroup.id
    }
    privateEndpointNetworkPolicies: 'Enabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = {
  parent: spokeVirtualNetwork
  name: 'snet-private-endpoints'
  properties: {
    addressPrefix: privateEndpointSubnetPrefix
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
  dependsOn: [
    aksSubnet
  ]
}

resource spokeToHubPeering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2025-07-01' = {
  parent: spokeVirtualNetwork
  name: 'peer-to-platform'
  properties: {
    allowForwardedTraffic: true
    allowGatewayTransit: true
    allowVirtualNetworkAccess: true
    remoteVirtualNetwork: {
      id: hubVirtualNetworkId
    }
    useRemoteGateways: false
  }
}

module hubToSpokePeering 'hub-peering.bicep' = {
  scope: resourceGroup(platformResourceGroupName)
  name: 'hub-peering-${namingToken}'
  params: {
    hubVirtualNetworkName: hubVirtualNetworkName
    spokeToken: namingToken
    spokeVirtualNetworkId: spokeVirtualNetwork.id
  }
}

module spokePrivateDnsLinks 'private-dns-links.bicep' = {
  scope: resourceGroup(platformResourceGroupName)
  name: 'private-dns-links-${namingToken}'
  params: {
    linkToken: namingToken
    privateDnsZoneNames: privateDnsZoneNames
    virtualNetworkId: spokeVirtualNetwork.id
  }
}

resource controlPlaneNetworkContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: spokeVirtualNetwork
  name: guid(spokeVirtualNetwork.id, controlPlaneIdentity.principalId, 'Network Contributor')
  properties: {
    principalId: controlPlaneIdentity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7')
  }
}

resource controlPlaneManagedIdentityOperator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kubeletUserAssignedIdentity
  name: guid(kubeletUserAssignedIdentity.id, 'Managed Identity Operator')
  properties: {
    principalId: controlPlaneIdentity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f1a07417-d97a-45cb-824c-7a7467783830')
  }
}

resource aksCluster 'Microsoft.ContainerService/managedClusters@2026-05-01' = {
  name: aksName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${controlPlaneUserAssignedIdentity.id}': {}
    }
  }
  sku: {
    name: 'Base'
    tier: 'Standard'
  }
  properties: {
    aadProfile: {
      adminGroupObjectIDs: aksAdminGroupObjectIds
      enableAzureRBAC: true
      managed: true
      tenantID: tenant().tenantId
    }
    addonProfiles: {
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          enableSecretRotation: 'true'
          rotationPollInterval: '2m'
        }
      }
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalyticsWorkspaceId
          useAADAuth: 'true'
        }
      }
    }
    agentPoolProfiles: [
      {
        name: 'system001'
        availabilityZones: availabilityZones
        count: systemNodeMinCount
        enableAutoScaling: true
        maxCount: systemNodeMaxCount
        maxPods: 110
        minCount: systemNodeMinCount
        mode: 'System'
        nodeTaints: [
          'CriticalAddonsOnly=true:NoSchedule'
        ]
        orchestratorVersion: empty(kubernetesVersion) ? null : kubernetesVersion
        osDiskSizeGB: 64
        osDiskType: 'Ephemeral'
        osSKU: 'AzureLinux'
        osType: 'Linux'
        scaleDownMode: 'Delete'
        type: 'VirtualMachineScaleSets'
        upgradeSettings: {
          maxSurge: '33%'
        }
        vmSize: systemNodeVmSize
        vnetSubnetID: aksSubnet.id
      }
    ]
    apiServerAccessProfile: {
      enablePrivateCluster: true
      enablePrivateClusterPublicFQDN: false
      privateDNSZone: aksPrivateDnsZoneId
    }
    autoScalerProfile: {
      'balance-similar-node-groups': 'true'
      expander: 'least-waste'
      'max-graceful-termination-sec': '600'
      'scale-down-unneeded-time': '10m'
      'scale-down-utilization-threshold': '0.5'
      'skip-nodes-with-local-storage': 'false'
    }
    azureMonitorProfile: {
      metrics: {
        enabled: true
        kubeStateMetrics: {
          metricAnnotationsAllowList: ''
          metricLabelsAllowlist: ''
        }
      }
    }
    dnsPrefix: 'aks-zava-${take(namingToken, 12)}'
    enableRBAC: true
    identityProfile: {
      kubeletidentity: {
        clientId: kubeletIdentity.clientId
        objectId: kubeletIdentity.principalId
        resourceId: kubeletUserAssignedIdentity.id
      }
    }
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    networkProfile: {
      dnsServiceIP: aksDnsServiceIp
      loadBalancerSku: 'standard'
      networkDataplane: 'cilium'
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'cilium'
      outboundType: 'userAssignedNATGateway'
      podCidr: aksPodCidr
      serviceCidr: aksServiceCidr
      advancedNetworking: {
        enabled: true
        observability: {
          enabled: true
        }
      }
    }
    nodeResourceGroup: take('MC_zava-contact-center-${take(location, 12)}_${take(aksName, 24)}', 80)
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: union({
      imageCleaner: {
        enabled: true
        intervalHours: 48
      }
      workloadIdentity: {
        enabled: true
      }
    }, enableDefender ? {
      defender: {
        logAnalyticsWorkspaceResourceId: logAnalyticsWorkspaceId
      }
    } : {})
    serviceMeshProfile: {
      mode: 'Istio'
      istio: {
        components: {
          ingressGateways: [
            {
              enabled: true
              mode: 'External'
            }
            {
              enabled: true
              mode: 'Internal'
            }
          ]
        }
      }
    }
    supportPlan: 'KubernetesOfficial'
  }
  dependsOn: [
    controlPlaneNetworkContributor
    hubToSpokePeering
    spokePrivateDnsLinks
    spokeToHubPeering
  ]
}

module managedPrometheus 'aks-prometheus.bicep' = {
  name: 'managed-prometheus-${namingToken}'
  params: {
    azureMonitorWorkspaceId: azureMonitorWorkspaceId
    azureMonitorWorkspaceLocation: azureMonitorWorkspaceLocation
    clusterLocation: location
    clusterName: aksCluster.name
    tags: tags
  }
  dependsOn: [
    aksCluster
  ]
}

resource voiceNodePool 'Microsoft.ContainerService/managedClusters/agentPools@2026-05-01' = {
  parent: aksCluster
  name: 'voice'
  properties: {
    availabilityZones: availabilityZones
    count: voiceNodeMinCount
    enableAutoScaling: true
    maxCount: voiceNodeMaxCount
    maxPods: 110
    minCount: voiceNodeMinCount
    mode: 'User'
    nodeLabels: {
      'zava.financial/workload': 'voice-edge'
    }
    nodeTaints: [
      'workload=zava-voice:NoSchedule'
    ]
    orchestratorVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    osDiskSizeGB: 128
    osDiskType: 'Ephemeral'
    osSKU: 'AzureLinux'
    osType: 'Linux'
    scaleDownMode: 'Delete'
    type: 'VirtualMachineScaleSets'
    upgradeSettings: {
      maxSurge: '33%'
    }
    vmSize: voiceNodeVmSize
    vnetSubnetID: aksSubnet.id
  }
}

resource istioGatewayNodePool 'Microsoft.ContainerService/managedClusters/agentPools@2026-05-01' = if (deployIstioGatewayNodePool) {
  parent: aksCluster
  name: 'istiogw'
  properties: {
    availabilityZones: availabilityZones
    count: istioGatewayNodeMinCount
    enableAutoScaling: true
    maxCount: istioGatewayNodeMaxCount
    maxPods: 110
    minCount: istioGatewayNodeMinCount
    mode: 'User'
    nodeLabels: {
      'azureservicemesh/istio.replica.preferred': 'true'
      'zava.financial/workload': 'istio-gateway'
    }
    orchestratorVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    osDiskSizeGB: 64
    osDiskType: 'Ephemeral'
    osSKU: 'AzureLinux'
    osType: 'Linux'
    scaleDownMode: 'Delete'
    type: 'VirtualMachineScaleSets'
    upgradeSettings: {
      maxSurge: '33%'
    }
    vmSize: istioGatewayNodeVmSize
    vnetSubnetID: aksSubnet.id
  }
}

resource gpuNodePool 'Microsoft.ContainerService/managedClusters/agentPools@2026-05-01' = if (deployGpuNodePool) {
  parent: aksCluster
  name: 'gpu'
  properties: {
    availabilityZones: availabilityZones
    count: gpuNodeMinCount
    enableAutoScaling: true
    maxCount: gpuNodeMaxCount
    maxPods: 30
    minCount: gpuNodeMinCount
    mode: 'User'
    nodeLabels: {
      'zava.financial/workload': 'gpu-inference'
    }
    nodeTaints: [
      'sku=gpu:NoSchedule'
    ]
    orchestratorVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    osDiskSizeGB: 128
    osDiskType: 'Managed'
    osSKU: 'AzureLinux'
    osType: 'Linux'
    scaleDownMode: 'Delete'
    type: 'VirtualMachineScaleSets'
    upgradeSettings: {
      maxSurge: '33%'
    }
    vmSize: gpuNodeVmSize
    vnetSubnetID: aksSubnet.id
  }
}

resource workloadFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: workloadUserAssignedIdentity
  name: 'fic-${workloadIdentityNamespace}-${workloadIdentityServiceAccount}'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aksCluster.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:${workloadIdentityNamespace}:${workloadIdentityServiceAccount}'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-02-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
    publicNetworkAccess: 'Disabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
    softDeleteRetentionInDays: 90
    tenantId: tenant().tenantId
  }
}

resource workloadKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workloadIdentity.principalId, 'Key Vault Secrets User')
  properties: {
    principalId: workloadIdentity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: storageSkuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        file: {
          enabled: true
          keyType: 'Account'
        }
      }
    }
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-04-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    containerDeleteRetentionPolicy: {
      days: 7
      enabled: true
    }
    deleteRetentionPolicy: {
      days: 7
      enabled: true
    }
  }
}

resource eventDeadLetterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-04-01' = {
  parent: blobService
  name: 'event-dead-letter'
  properties: {
    publicAccess: 'None'
  }
}

resource workloadStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccount
  name: guid(storageAccount.id, workloadIdentity.principalId, 'Storage Blob Data Contributor')
  properties: {
    principalId: workloadIdentity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource appConfiguration 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
  name: appConfigurationName
  location: location
  tags: tags
  sku: {
    name: 'standard'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    disableLocalAuth: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    softDeleteRetentionInDays: 7
  }
}

resource workloadAppConfigurationReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appConfiguration
  name: guid(appConfiguration.id, workloadIdentity.principalId, 'App Configuration Data Reader')
  properties: {
    principalId: workloadIdentity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '516239f1-63e1-4d78-a4de-a74fb236a071')
  }
}


module keyVaultPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'key-vault-private-endpoint'
  params: {
    groupIds: [
      'vault'
    ]
    location: location
    name: 'pep-${keyVault.name}'
    privateDnsZoneIds: [
      privateDnsZoneIds.keyVault
    ]
    privateLinkServiceId: keyVault.id
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
  dependsOn: [
    spokePrivateDnsLinks
  ]
}

module storageBlobPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'storage-blob-private-endpoint'
  params: {
    groupIds: [
      'blob'
    ]
    location: location
    name: 'pep-${storageAccount.name}-blob'
    privateDnsZoneIds: [
      privateDnsZoneIds.storageBlob
    ]
    privateLinkServiceId: storageAccount.id
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
  dependsOn: [
    spokePrivateDnsLinks
  ]
}

module storageQueuePrivateEndpoint 'private-endpoint.bicep' = {
  name: 'storage-queue-private-endpoint'
  params: {
    groupIds: [
      'queue'
    ]
    location: location
    name: 'pep-${storageAccount.name}-queue'
    privateDnsZoneIds: [
      privateDnsZoneIds.storageQueue
    ]
    privateLinkServiceId: storageAccount.id
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
  dependsOn: [
    spokePrivateDnsLinks
  ]
}

module appConfigurationPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'app-configuration-private-endpoint'
  params: {
    groupIds: [
      'configurationStores'
    ]
    location: location
    name: 'pep-${appConfiguration.name}'
    privateDnsZoneIds: [
      privateDnsZoneIds.appConfiguration
    ]
    privateLinkServiceId: appConfiguration.id
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
  dependsOn: [
    spokePrivateDnsLinks
  ]
}

module cosmosPrivateEndpoint 'private-endpoint.bicep' = {
  name: 'cosmos-private-endpoint'
  params: {
    groupIds: [
      'Sql'
    ]
    location: location
    name: 'pep-cosmos-${namingToken}'
    privateDnsZoneIds: [
      privateDnsZoneIds.cosmosSql
    ]
    privateLinkServiceId: cosmosAccountId
    subnetId: privateEndpointSubnet.id
    tags: tags
  }
  dependsOn: [
    spokePrivateDnsLinks
  ]
}



resource aksDiagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-shared-log-analytics'
  scope: aksCluster
  properties: {
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
    workspaceId: logAnalyticsWorkspaceId
  }
}

resource keyVaultDiagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-shared-log-analytics'
  scope: keyVault
  properties: {
    logs: [
      {
        categoryGroup: 'audit'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
    workspaceId: logAnalyticsWorkspaceId
  }
}

output regionIndex int = regionIndex
output location string = location
output resourceGroupName string = resourceGroup().name
output virtualNetworkId string = spokeVirtualNetwork.id
output virtualNetworkName string = spokeVirtualNetwork.name
output privateEndpointSubnetId string = privateEndpointSubnet.id
output aksClusterId string = aksCluster.id
output aksClusterName string = aksCluster.name
output aksOidcIssuerUrl string = aksCluster.properties.oidcIssuerProfile.issuerURL
output prometheusDataCollectionRuleId string = managedPrometheus.outputs.dataCollectionRuleId
output workloadIdentityClientId string = workloadIdentity.clientId
output keyVaultName string = keyVault.name
output storageAccountName string = storageAccount.name
output appConfigurationEndpoint string = appConfiguration.properties.endpoint
