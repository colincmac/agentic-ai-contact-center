targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('AZD environment name used for tags and globally unique resource names.')
param environmentName string

@description('Name of the connectivity-style platform resource group.')
param platformResourceGroupName string = 'zava-platform'

@minLength(1)
@description('Primary Azure region for the platform and first application stamp.')
param location string

@description('JSON array of additional Azure regions. The primary location is added automatically.')
param additionalLocationsJson string = '[]'

@description('Region used for Microsoft Foundry model deployments when it differs from the primary region.')
param aiDeploymentsLocation string = location

@description('ID of the user or application assigned deployment-time data-plane roles.')
param principalId string

@description('Principal type of the deployment principal.')
param principalType string

@description('Optional salt used to diversify globally unique resource names across recreations.')
param resourceTokenSalt string = ''

@description('Optional existing AI Services account name in zava-platform.')
param aiFoundryResourceName string = ''

@description('Optional name of the Microsoft Foundry project. An environment-specific name is generated when empty.')
param aiFoundryProjectName string = ''

@description('JSON array of model deployments.')
param aiProjectDeploymentsJson string = '[]'

@description('JSON array of Microsoft Foundry project connections.')
param aiProjectConnectionsJson string = '[]'

@secure()
@description('JSON map of Microsoft Foundry connection names to credential objects.')
param aiProjectConnectionCredentialsJson string = '{}'

@description('JSON array of optional Microsoft Foundry dependent resources.')
param aiProjectDependentResourcesJson string = '[]'

@description('Enable hosted agent deployment. Hosted agents retain public AI account access until private hosted-agent networking is added.')
param enableHostedAgents bool = false

@description('Enable the Foundry capability host for agent conversations.')
param enableCapabilityHost bool = true

@description('Enable monitoring for the Foundry project.')
param enableMonitoring bool = true

@description('Reference an existing Microsoft Foundry project instead of provisioning a new project.')
param useExistingAiProject bool = false

@description('Optional existing Premium ACR resource ID. When empty, the platform module creates a registry.')
param existingContainerRegistryResourceId string = ''

@description('Optional login server for the existing ACR.')
param existingContainerRegistryEndpoint string = ''

@description('Optional name of an existing ACR connection on the Foundry project.')
param existingAcrConnectionName string = ''

@secure()
@description('Optional existing Application Insights connection string used by an existing Foundry project.')
param existingApplicationInsightsConnectionString string = ''

@description('Optional existing Application Insights resource ID.')
param existingApplicationInsightsResourceId string = ''

@description('Optional existing Application Insights connection name on the Foundry project.')
param existingAppInsightsConnectionName string = ''

@description('API Management publisher name.')
param apiManagementPublisherName string = 'Zava Financial'

@description('API Management publisher email.')
param apiManagementPublisherEmail string = 'platform@example.com'

@description('Deploy Azure Bastion in the platform hub VNet.')
param deployBastion bool = true

@description('Azure Communication Services data geography used by every regional ACS resource.')
param communicationServicesDataLocation string = 'United States'

@description('Storage replication SKU used by regional application storage accounts.')
param storageSkuName string = 'Standard_ZRS'

@description('Azure Managed Redis SKU used by every regional stamp.')
param redisSkuName string = 'Balanced_B1'

@description('Kubernetes version. Empty selects the regional AKS default.')
param kubernetesVersion string = ''

@description('Availability zones used by AKS node pools. Use an empty array in regions without zone support.')
param aksAvailabilityZonesJson string = '[]'

@description('Microsoft Entra group object IDs with AKS administrator access.')
param aksAdminGroupObjectIdsJson string = '[]'

@description('VM size for AKS system pools.')
param systemNodeVmSize string = 'Standard_D4ds_v5'

@minValue(1)
@description('Minimum system nodes per cluster.')
param systemNodeMinCount int = 3

@minValue(1)
@description('Maximum system nodes per cluster.')
param systemNodeMaxCount int = 6

@description('VM size for voice-edge node pools.')
param voiceNodeVmSize string = 'Standard_D16ds_v5'

@minValue(1)
@description('Minimum voice-edge nodes per cluster.')
param voiceNodeMinCount int = 1

@minValue(1)
@description('Maximum voice-edge nodes per cluster.')
param voiceNodeMaxCount int = 3

@description('Deploy the dedicated Istio gateway pool from ADR-0015.')
param deployIstioGatewayNodePool bool = true

@description('VM size for dedicated Istio gateway pools.')
param istioGatewayNodeVmSize string = 'Standard_D8ds_v6'

@minValue(1)
@description('Minimum dedicated Istio gateway nodes per cluster.')
param istioGatewayNodeMinCount int = 3

@minValue(1)
@description('Maximum dedicated Istio gateway nodes per cluster.')
param istioGatewayNodeMaxCount int = 12

@description('Deploy an optional GPU pool in every regional AKS cluster.')
param deployGpuNodePool bool = false

@description('VM size for optional GPU node pools.')
param gpuNodeVmSize string = 'Standard_NC4as_T4_v3'

@minValue(0)
@description('Minimum GPU nodes per cluster.')
param gpuNodeMinCount int = 0

@minValue(1)
@description('Maximum GPU nodes per cluster.')
param gpuNodeMaxCount int = 3

@description('Enable Microsoft Defender for Containers on every AKS cluster.')
param enableDefender bool = false

@description('Bot messaging endpoint. Leave empty until the application ingress is deployed.')
param botMessagingEndpoint string = ''

var additionalLocations = json(additionalLocationsJson)
var stampLocations = union([
  location
], additionalLocations)
var aksAvailabilityZones = json(aksAvailabilityZonesJson)
var aksAdminGroupObjectIds = json(aksAdminGroupObjectIdsJson)
var resolvedAiFoundryProjectName = empty(aiFoundryProjectName) ? 'ai-project-${environmentName}' : aiFoundryProjectName
var resourceToken = empty(resourceTokenSalt)
  ? uniqueString(subscription().id, environmentName)
  : uniqueString(subscription().id, environmentName, resourceTokenSalt)
var stampNamingTokens = [
  for stampLocation in stampLocations: '${take(replace(stampLocation, '-', ''), 10)}-${take(uniqueString(subscription().id, environmentName, stampLocation), 6)}'
]
var stampNetworks = [
  for (stampLocation, index) in stampLocations: {
    spokeAddressPrefix: '10.${10 + index}.0.0/16'
    aksSubnetPrefix: '10.${10 + index}.0.0/20'
    privateEndpointSubnetPrefix: '10.${10 + index}.16.0/24'
    serviceCidr: '10.${100 + index}.0.0/16'
    dnsServiceIp: '10.${100 + index}.0.10'
    podCidr: '192.168.${index * 16}.0/20'
  }
]
var aiProjectDeployments = json(aiProjectDeploymentsJson)
var aiProjectConnections = json(aiProjectConnectionsJson)
var aiProjectConnectionCredentials = json(aiProjectConnectionCredentialsJson)
var requestedAiProjectDependentResources = json(aiProjectDependentResourcesJson)
var aiProjectDependentResources = filter(requestedAiProjectDependentResources, dependentResource => dependentResource.resource != 'registry')
var baseTags = {
  company: 'Zava Financial'
  environment: environmentName
  managedBy: 'azd'
  workload: 'contact-center'
}

resource platformResourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: platformResourceGroupName
  location: location
  tags: union(baseTags, {
    landingZoneRole: 'platform-connectivity'
  })
}

resource regionalResourceGroups 'Microsoft.Resources/resourceGroups@2025-04-01' = [
  for stampLocation in stampLocations: {
    name: 'zava-contact-center-${stampLocation}'
    location: stampLocation
    tags: union(baseTags, {
      landingZoneRole: 'application'
      region: stampLocation
    })
  }
]

var containerRegistryName = take('crzavaplatform${resourceToken}', 50)
var apiManagementName = take('apim-zava-${environmentName}-${take(resourceToken, 6)}', 50)
var logAnalyticsWorkspaceName = take('log-zava-shared-${location}', 63)
var applicationInsightsName = take('appi-zava-shared-${location}', 260)
var azureMonitorWorkspaceName = take('amw-zava-shared-${location}', 63)
var managedGrafanaName = take('amg-zava-${environmentName}-${take(resourceToken, 6)}', 30)
var hubVirtualNetworkName = 'vnet-connectivity-${location}'

module platform 'modules/platform.bicep' = {
  scope: platformResourceGroup
  name: 'zava-platform'
  params: {
    apiManagementName: apiManagementName
    apiManagementPublisherEmail: apiManagementPublisherEmail
    apiManagementPublisherName: apiManagementPublisherName
    applicationInsightsName: applicationInsightsName
    azureMonitorWorkspaceName: azureMonitorWorkspaceName
    containerRegistryName: containerRegistryName
    deployBastion: deployBastion
    existingContainerRegistryEndpoint: existingContainerRegistryEndpoint
    existingContainerRegistryResourceId: existingContainerRegistryResourceId
    hubVirtualNetworkName: hubVirtualNetworkName
    location: location
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    managedGrafanaName: managedGrafanaName
    principalId: principalId
    principalType: principalType
    stampLocations: stampLocations
    tags: union(baseTags, {
      resourceGroup: platformResourceGroup.name
    })
  }
}

var resolvedApplicationInsightsConnectionString = empty(existingApplicationInsightsConnectionString)
  ? platform.outputs.applicationInsightsConnectionString
  : existingApplicationInsightsConnectionString
var resolvedApplicationInsightsResourceId = empty(existingApplicationInsightsResourceId)
  ? platform.outputs.applicationInsightsId
  : existingApplicationInsightsResourceId

module aiProject 'core/ai/ai-project.bicep' = if (!useExistingAiProject) {
  scope: platformResourceGroup
  name: 'ai-project'
  params: {
    additionalDependentResources: aiProjectDependentResources
    aiFoundryProjectName: resolvedAiFoundryProjectName
    connectionCredentials: aiProjectConnectionCredentials
    connections: aiProjectConnections
    deployments: aiProjectDeployments
    enableCapabilityHost: enableCapabilityHost
    enableHostedAgents: enableHostedAgents
    enableMonitoring: enableMonitoring
    existingAcrConnectionName: existingAcrConnectionName
    existingAiAccountName: aiFoundryResourceName
    existingApplicationInsightsConnectionString: resolvedApplicationInsightsConnectionString
    existingApplicationInsightsResourceId: resolvedApplicationInsightsResourceId
    existingAppInsightsConnectionName: existingAppInsightsConnectionName
    existingContainerRegistryEndpoint: platform.outputs.containerRegistryEndpoint
    existingContainerRegistryResourceId: platform.outputs.containerRegistryId
    location: aiDeploymentsLocation
    networkAclsDefaultAction: enableHostedAgents ? 'Allow' : 'Deny'
    principalId: principalId
    principalType: principalType
    publicNetworkAccess: enableHostedAgents ? 'Enabled' : 'Disabled'
    resourceTokenSalt: resourceTokenSalt
    tags: union(baseTags, {
      resourceGroup: platformResourceGroup.name
    })
  }
}

module existingAiProject 'core/ai/existing-ai-project.bicep' = if (useExistingAiProject) {
  scope: platformResourceGroup
  name: 'existing-ai-project'
  params: {
    aiFoundryProjectName: resolvedAiFoundryProjectName
    aiServicesAccountName: aiFoundryResourceName
    connectionCredentials: aiProjectConnectionCredentials
    connections: aiProjectConnections
    deployments: aiProjectDeployments
    existingAcrConnectionName: existingAcrConnectionName
    existingApplicationInsightsConnectionString: resolvedApplicationInsightsConnectionString
    existingApplicationInsightsResourceId: resolvedApplicationInsightsResourceId
    existingContainerRegistryEndpoint: platform.outputs.containerRegistryEndpoint
  }
}

var aiAccountId = useExistingAiProject ? existingAiProject.outputs.accountId : aiProject.outputs.accountId
var aiAccountName = useExistingAiProject ? existingAiProject.outputs.aiServicesAccountName : aiProject.outputs.aiServicesAccountName
var aiProjectId = useExistingAiProject ? existingAiProject.outputs.projectId : aiProject.outputs.projectId
var aiProjectName = useExistingAiProject ? existingAiProject.outputs.projectName : aiProject.outputs.projectName
var aiProjectPrincipalId = useExistingAiProject ? existingAiProject.outputs.projectPrincipalId : aiProject.outputs.projectPrincipalId
var aiProjectEndpoint = useExistingAiProject ? existingAiProject.outputs.AZURE_AI_PROJECT_ENDPOINT : aiProject.outputs.AZURE_AI_PROJECT_ENDPOINT
var openAiEndpoint = useExistingAiProject ? existingAiProject.outputs.AZURE_OPENAI_ENDPOINT : aiProject.outputs.AZURE_OPENAI_ENDPOINT

module aiServicesPrivateEndpoint 'modules/private-endpoint.bicep' = {
  scope: platformResourceGroup
  name: 'ai-services-private-endpoint'
  params: {
    groupIds: [
      'account'
    ]
    location: location
    name: 'pep-${take(aiAccountName, 54)}'
    privateDnsZoneIds: [
      platform.outputs.privateDnsZoneIds.aiServices
      platform.outputs.privateDnsZoneIds.openAi
      platform.outputs.privateDnsZoneIds.cognitiveServices
    ]
    privateLinkServiceId: aiAccountId
    subnetId: platform.outputs.privateEndpointSubnetId
    tags: union(baseTags, {
      resourceGroup: platformResourceGroup.name
    })
  }
}

var cosmosAccountName = take('cosmos-zava-contact-${resourceToken}', 44)
var botServiceName = take('bot-zava-contact-center-${resourceToken}', 64)

module globalData 'modules/global-data.bicep' = {
  scope: regionalResourceGroups[0]
  name: 'global-contact-center-data'
  params: {
    botMessagingEndpoint: botMessagingEndpoint
    botServiceName: botServiceName
    cosmosAccountName: cosmosAccountName
    location: location
    logAnalyticsWorkspaceId: platform.outputs.logAnalyticsWorkspaceId
    resourceToken: resourceToken
    stampLocations: stampLocations
    tags: union(baseTags, {
      resourceGroup: regionalResourceGroups[0].name
      region: location
    })
  }
}

module regionalIdentities 'modules/regional-identities.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: regionalResourceGroups[index]
    name: 'regional-identities-${stampNamingTokens[index]}'
    params: {
      location: stampLocation
      namingToken: stampNamingTokens[index]
      tags: union(baseTags, {
        resourceGroup: regionalResourceGroups[index].name
        region: stampLocation
      })
    }
  }
]

module platformRbac 'modules/platform-rbac.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: platformResourceGroup
    name: 'platform-rbac-${stampNamingTokens[index]}'
    params: {
      aiServicesAccountName: aiAccountName
      aksPrivateDnsZoneName: 'privatelink.${stampLocation}.azmk8s.io'
      applicationInsightsName: platform.outputs.applicationInsightsName
      assignFoundryProjectLogReader: index == 0
      containerRegistryName: platform.outputs.containerRegistryName
      controlPlanePrincipalId: regionalIdentities[index].outputs.controlPlane.principalId
      foundryProjectPrincipalId: aiProjectPrincipalId
      kubeletPrincipalId: regionalIdentities[index].outputs.kubelet.principalId
      workloadPrincipalId: regionalIdentities[index].outputs.workload.principalId
    }
  }
]

module cosmosRbac 'modules/cosmos-rbac.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: regionalResourceGroups[0]
    name: 'cosmos-rbac-${stampNamingTokens[index]}'
    params: {
      cosmosAccountName: globalData.outputs.cosmosAccountName
      workloadPrincipalId: regionalIdentities[index].outputs.workload.principalId
    }
  }
]

@batchSize(1)
module regionalStamps 'modules/regional-stamp.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: regionalResourceGroups[index]
    name: 'regional-stamp-${stampNamingTokens[index]}'
    params: {
      aksAdminGroupObjectIds: aksAdminGroupObjectIds
      aksDnsServiceIp: stampNetworks[index].dnsServiceIp
      aksPodCidr: stampNetworks[index].podCidr
      aksPrivateDnsZoneId: platform.outputs.aksPrivateDnsZoneIds[index]
      aksServiceCidr: stampNetworks[index].serviceCidr
      aksSubnetPrefix: stampNetworks[index].aksSubnetPrefix
      availabilityZones: aksAvailabilityZones
      azureMonitorWorkspaceId: platform.outputs.azureMonitorWorkspaceId
      azureMonitorWorkspaceLocation: platform.outputs.azureMonitorWorkspaceLocation
      communicationServicesDataLocation: communicationServicesDataLocation
      controlPlaneIdentity: regionalIdentities[index].outputs.controlPlane
      cosmosAccountId: globalData.outputs.cosmosAccountId
      deployGpuNodePool: deployGpuNodePool
      deployIstioGatewayNodePool: deployIstioGatewayNodePool
      enableDefender: enableDefender
      gpuNodeMaxCount: gpuNodeMaxCount
      gpuNodeMinCount: gpuNodeMinCount
      gpuNodeVmSize: gpuNodeVmSize
      hubVirtualNetworkId: platform.outputs.hubVirtualNetworkId
      hubVirtualNetworkName: platform.outputs.hubVirtualNetworkName
      istioGatewayNodeMaxCount: istioGatewayNodeMaxCount
      istioGatewayNodeMinCount: istioGatewayNodeMinCount
      istioGatewayNodeVmSize: istioGatewayNodeVmSize
      kubeletIdentity: regionalIdentities[index].outputs.kubelet
      kubernetesVersion: kubernetesVersion
      location: stampLocation
      logAnalyticsWorkspaceId: platform.outputs.logAnalyticsWorkspaceId
      namingToken: stampNamingTokens[index]
      platformResourceGroupName: platformResourceGroup.name
      privateDnsZoneIds: {
        appConfiguration: platform.outputs.privateDnsZoneIds.appConfiguration
        cosmosSql: platform.outputs.privateDnsZoneIds.cosmosSql
        keyVault: platform.outputs.privateDnsZoneIds.keyVault
        redis: platform.outputs.privateDnsZoneIds.redis
        storageBlob: platform.outputs.privateDnsZoneIds.storageBlob
        storageQueue: platform.outputs.privateDnsZoneIds.storageQueue
      }
      privateDnsZoneNames: platform.outputs.privateDnsZoneNames
      privateEndpointSubnetPrefix: stampNetworks[index].privateEndpointSubnetPrefix
      redisSkuName: redisSkuName
      regionIndex: index
      spokeAddressPrefix: stampNetworks[index].spokeAddressPrefix
      storageSkuName: storageSkuName
      systemNodeMaxCount: systemNodeMaxCount
      systemNodeMinCount: systemNodeMinCount
      systemNodeVmSize: systemNodeVmSize
      tags: union(baseTags, {
        resourceGroup: regionalResourceGroups[index].name
        region: stampLocation
      })
      voiceNodeMaxCount: voiceNodeMaxCount
      voiceNodeMinCount: voiceNodeMinCount
      voiceNodeVmSize: voiceNodeVmSize
      workloadIdentity: regionalIdentities[index].outputs.workload
    }
    dependsOn: [
      cosmosRbac
      platformRbac
    ]
  }
]

@batchSize(1)
module redisGeoReplication 'modules/redis-geo-replication.bicep' = [
  for (stampLocation, index) in stampLocations: if (length(stampLocations) > 1) {
    scope: regionalResourceGroups[index]
    name: 'redis-geo-${stampNamingTokens[index]}'
    params: {
      groupNickname: 'zava-contact-center-${take(resourceToken, 8)}'
      linkedDatabaseIds: [
        for (linkedStampLocation, linkedIndex) in stampLocations: regionalStamps[linkedIndex].outputs.redisDatabaseId
      ]
      redisClusterName: regionalStamps[index].outputs.redisClusterName
    }
  }
]

output AZURE_RESOURCE_GROUP string = regionalResourceGroups[0].name
output AZURE_PLATFORM_RESOURCE_GROUP string = platformResourceGroup.name
output AZURE_REGIONAL_RESOURCE_GROUPS_JSON array = [
  for (stampLocation, index) in stampLocations: regionalResourceGroups[index].name
]
output AZURE_AI_ACCOUNT_ID string = aiAccountId
output AZURE_AI_PROJECT_ID string = aiProjectId
output AZURE_AI_FOUNDRY_PROJECT_ID string = aiProjectId
output AZURE_AI_ACCOUNT_NAME string = aiAccountName
output AZURE_AI_PROJECT_NAME string = aiProjectName
output AZURE_AI_PROJECT_ENDPOINT string = aiProjectEndpoint
output FOUNDRY_PROJECT_ENDPOINT string = aiProjectEndpoint
output AZURE_OPENAI_ENDPOINT string = openAiEndpoint
output APPLICATIONINSIGHTS_CONNECTION_STRING string = platform.outputs.applicationInsightsConnectionString
output APPLICATIONINSIGHTS_RESOURCE_ID string = platform.outputs.applicationInsightsId
output AZURE_MONITOR_WORKSPACE_ID string = platform.outputs.azureMonitorWorkspaceId
output AZURE_MONITOR_WORKSPACE_NAME string = platform.outputs.azureMonitorWorkspaceName
output AZURE_MANAGED_GRAFANA_ID string = platform.outputs.managedGrafanaId
output AZURE_MANAGED_GRAFANA_NAME string = platform.outputs.managedGrafanaName
output AZURE_MANAGED_GRAFANA_ENDPOINT string = platform.outputs.managedGrafanaEndpoint
output AZURE_AI_PROJECT_ACR_CONNECTION_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.registry.connectionName
  : aiProject.outputs.dependentResources.registry.connectionName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = platform.outputs.containerRegistryEndpoint
output AZURE_CONTAINER_REGISTRY_RESOURCE_ID string = platform.outputs.containerRegistryId
output AZURE_COSMOS_DB_ACCOUNT_NAME string = globalData.outputs.cosmosAccountName
output AZURE_COSMOS_DB_ENDPOINT string = globalData.outputs.cosmosEndpoint
output AZURE_BOT_SERVICE_NAME string = globalData.outputs.botServiceName
output AZURE_AKS_CLUSTERS_JSON array = [
  for (stampLocation, index) in stampLocations: {
    id: regionalStamps[index].outputs.aksClusterId
    location: stampLocation
    name: regionalStamps[index].outputs.aksClusterName
    resourceGroup: regionalResourceGroups[index].name
  }
]
output AZURE_REDIS_CLUSTERS_JSON array = [
  for (stampLocation, index) in stampLocations: {
    id: regionalStamps[index].outputs.redisClusterId
    location: stampLocation
    name: regionalStamps[index].outputs.redisClusterName
    resourceGroup: regionalResourceGroups[index].name
  }
]
output AZURE_COMMUNICATION_SERVICES_JSON array = [
  for (stampLocation, index) in stampLocations: {
    location: stampLocation
    name: regionalStamps[index].outputs.communicationServicesName
    resourceGroup: regionalResourceGroups[index].name
  }
]
output AZURE_AI_SEARCH_CONNECTION_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.search.connectionName
  : aiProject.outputs.dependentResources.search.connectionName
output AZURE_AI_SEARCH_SERVICE_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.search.serviceName
  : aiProject.outputs.dependentResources.search.serviceName
output AZURE_STORAGE_CONNECTION_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.storage.connectionName
  : aiProject.outputs.dependentResources.storage.connectionName
output AZURE_STORAGE_ACCOUNT_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.storage.accountName
  : aiProject.outputs.dependentResources.storage.accountName
output BING_GROUNDING_CONNECTION_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_grounding.connectionName
  : aiProject.outputs.dependentResources.bing_grounding.connectionName
output BING_GROUNDING_RESOURCE_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_grounding.name
  : aiProject.outputs.dependentResources.bing_grounding.name
output BING_GROUNDING_CONNECTION_ID string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_grounding.connectionId
  : aiProject.outputs.dependentResources.bing_grounding.connectionId
output BING_CUSTOM_GROUNDING_CONNECTION_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_custom_grounding.connectionName
  : aiProject.outputs.dependentResources.bing_custom_grounding.connectionName
output BING_CUSTOM_GROUNDING_NAME string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_custom_grounding.name
  : aiProject.outputs.dependentResources.bing_custom_grounding.name
output BING_CUSTOM_GROUNDING_CONNECTION_ID string = useExistingAiProject
  ? existingAiProject.outputs.dependentResources.bing_custom_grounding.connectionId
  : aiProject.outputs.dependentResources.bing_custom_grounding.connectionId
output AI_PROJECT_CONNECTION_IDS_JSON string = useExistingAiProject
  ? string(existingAiProject.outputs.connectionIds)
  : string(aiProject.outputs.connectionIds)
