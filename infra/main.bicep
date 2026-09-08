targetScope = 'subscription'

import { getShortLocation } from './core/helpers.bicep'

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

@description('ID of the user or application assigned deployment-time data-plane roles.')
param principalId string

@description('Principal type of the deployment principal.')
param principalType string

@description('Existing AI Services account name used when useExistingAiProject is enabled.')
param aiFoundryResourceName string = ''

@description('Existing Microsoft Foundry project name used when useExistingAiProject is enabled.')
param aiFoundryProjectName string = ''

@description('Optional base name for newly provisioned regional Microsoft Foundry projects.')
param aiFoundryProjectBaseName string = ''

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

@description('Reference an existing Microsoft Foundry project for the first region; projects for additional regions are provisioned.')
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
param redisSkuName string = 'Balanced_B10'

@description('Kubernetes version. Empty selects the regional AKS default.')
param kubernetesVersion string = ''

@description('Availability zones used by AKS node pools. Use an empty array in regions without zone support.')
param aksAvailabilityZonesJson string = '[]'

@description('Microsoft Entra group object IDs with AKS administrator access.')
param aksAdminGroupObjectIdsJson string = '[]'

@description('VM size for AKS system pools.')
param systemNodeVmSize string = 'Standard_D4ds_v7'

@minValue(1)
@description('Minimum system nodes per cluster.')
param systemNodeMinCount int = 3

@minValue(1)
@description('Maximum system nodes per cluster.')
param systemNodeMaxCount int = 6

@description('VM size for voice-edge node pools.')
param voiceNodeVmSize string = 'Standard_D16ds_v7'

@minValue(1)
@description('Minimum voice-edge nodes per cluster.')
param voiceNodeMinCount int = 1

@minValue(1)
@description('Maximum voice-edge nodes per cluster.')
param voiceNodeMaxCount int = 3

@description('Deploy the dedicated Istio gateway pool from ADR-0015.')
param deployIstioGatewayNodePool bool = true

@description('VM size for dedicated Istio gateway pools.')
param istioGatewayNodeVmSize string = 'Standard_D8ds_v7'

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
var resolvedAiFoundryProjectBaseName = empty(aiFoundryProjectBaseName)
  ? 'ai-project-${environmentName}'
  : aiFoundryProjectBaseName
var resolvedExistingAiFoundryProjectName = empty(aiFoundryProjectName)
  ? resolvedAiFoundryProjectBaseName
  : aiFoundryProjectName
var normalizedEnvironmentName = toLower(environmentName)
var compactEnvironmentName = replace(normalizedEnvironmentName, '-', '')
var stampNamingTokens = [
  for stampLocation in stampLocations: '${take(replace(getShortLocation(stampLocation), '-', ''), 10)}-${take(uniqueString(subscription().id, environmentName, stampLocation), 6)}'
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

var shortLocation = getShortLocation(location)
var primaryRegionalResourceGroupName = 'zava-contact-center-${shortLocation}'

resource platformResourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: platformResourceGroupName
  location: location
  tags: union(baseTags, {
    landingZoneRole: 'platform-connectivity'
  })
}

resource regionalResourceGroups 'Microsoft.Resources/resourceGroups@2025-04-01' = [
  for stampLocation in stampLocations: {
    name: 'zava-contact-center-${getShortLocation(stampLocation)}'
    location: stampLocation
    tags: union(baseTags, {
      landingZoneRole: 'application'
      region: stampLocation
    })
  }
]

var containerRegistryName = take('crzavaplatform${compactEnvironmentName}', 50)
var apiManagementName = take('apim-zava-${normalizedEnvironmentName}', 50)
var logAnalyticsWorkspaceName = take('log-zava-shared-${shortLocation}', 63)
var applicationInsightsName = take('appi-zava-shared-${shortLocation}', 260)
var azureMonitorWorkspaceName = take('amw-zava-shared-${shortLocation}', 63)
var managedGrafanaName = take('amg-zava-${normalizedEnvironmentName}', 23)
var hubVirtualNetworkName = 'vnet-connectivity-${shortLocation}'

var cosmosAccountName = take('cosmos-zava-contact-center-${normalizedEnvironmentName}', 44)
var botServiceName = take('bot-zava-contact-center-${normalizedEnvironmentName}', 64)

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
    communicationServicesDataLocation: communicationServicesDataLocation
    cosmosAccountName: cosmosAccountName
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

module regionalAiProjects 'modules/regional-ai-project.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: resourceGroup((useExistingAiProject && index == 0) ? platformResourceGroup.name : regionalResourceGroups[index].name)
    name: 'regional-ai-project-${stampNamingTokens[index]}'
    params: {
      additionalDependentResources: aiProjectDependentResources
      aiFoundryProjectName: (useExistingAiProject && index == 0)
        ? resolvedExistingAiFoundryProjectName
        : take('${getShortLocation(stampLocation)}-${resolvedAiFoundryProjectBaseName}', 64)
      connectionCredentials: aiProjectConnectionCredentials
      connections: aiProjectConnections
      deployments: aiProjectDeployments
      enableCapabilityHost: enableCapabilityHost
      enableHostedAgents: enableHostedAgents
      enableMonitoring: enableMonitoring
      environmentName: '${stampNamingTokens[index]}-${environmentName}'
      existingAcrConnectionName: (useExistingAiProject && index == 0) ? existingAcrConnectionName : ''
      existingAiAccountName: (useExistingAiProject && index == 0) ? aiFoundryResourceName : ''
      existingApplicationInsightsConnectionString: resolvedApplicationInsightsConnectionString
      existingApplicationInsightsResourceId: resolvedApplicationInsightsResourceId
      existingAppInsightsConnectionName: (useExistingAiProject && index == 0) ? existingAppInsightsConnectionName : ''
      existingContainerRegistryEndpoint: platform.outputs.containerRegistryEndpoint
      existingContainerRegistryResourceId: platform.outputs.containerRegistryId
      location: stampLocation
      networkAclsDefaultAction: enableHostedAgents ? 'Allow' : 'Deny'
      principalId: principalId
      principalType: principalType
      publicNetworkAccess: enableHostedAgents ? 'Enabled' : 'Disabled'
      tags: union(baseTags, {
        resourceGroup: (useExistingAiProject && index == 0)
          ? platformResourceGroup.name
          : regionalResourceGroups[index].name
        region: stampLocation
      })
      useExistingAiProject: useExistingAiProject && index == 0
    }
  }
]

var aiAccountId = regionalAiProjects[0].outputs.accountId
var aiAccountName = regionalAiProjects[0].outputs.aiServicesAccountName
var aiProjectId = regionalAiProjects[0].outputs.projectId
var aiProjectName = regionalAiProjects[0].outputs.projectName
var aiProjectEndpoint = regionalAiProjects[0].outputs.AZURE_AI_PROJECT_ENDPOINT
var openAiEndpoint = regionalAiProjects[0].outputs.AZURE_OPENAI_ENDPOINT
var speechToTextEndpoint = regionalAiProjects[0].outputs.speechToTextEndpoint
var textToSpeechEndpoint = regionalAiProjects[0].outputs.textToSpeechEndpoint
var voiceLiveEndpoint = regionalAiProjects[0].outputs.voiceLiveEndpoint

module aiServicesPrivateEndpoints 'modules/private-endpoint.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: regionalResourceGroups[index]
    name: 'ai-services-private-endpoint-${stampNamingTokens[index]}'
    params: {
      groupIds: [
        'account'
      ]
      location: stampLocation
      name: 'pep-${take(regionalAiProjects[index].outputs.aiServicesAccountName, 54)}'
      privateDnsZoneIds: [
        platform.outputs.privateDnsZoneIds.aiServices
        platform.outputs.privateDnsZoneIds.openAi
        platform.outputs.privateDnsZoneIds.cognitiveServices
      ]
      privateLinkServiceId: regionalAiProjects[index].outputs.accountId
      subnetId: regionalStamps[index].outputs.privateEndpointSubnetId
      tags: union(baseTags, {
        resourceGroup: regionalResourceGroups[index].name
        region: stampLocation
      })
    }
  }
]

module globalData 'modules/global-data.bicep' = {
  scope: resourceGroup(primaryRegionalResourceGroupName)
  name: 'global-contact-center-data'
  params: {
    botMessagingEndpoint: botMessagingEndpoint
    botServiceName: botServiceName
    environmentName: environmentName
    location: location
    tags: union(baseTags, {
      resourceGroup: primaryRegionalResourceGroupName
      region: location
    })
  }
  dependsOn: [
    regionalResourceGroups[0]
  ]
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

module regionalAiRbac 'modules/ai-rbac.bicep' = [
  for (_, index) in stampLocations: {
    scope: resourceGroup(
      (useExistingAiProject && index == 0) ? platformResourceGroup.name : regionalResourceGroups[index].name
    )
    name: 'ai-rbac-${stampNamingTokens[index]}'
    params: {
      aiServicesAccountName: regionalAiProjects[index].outputs.aiServicesAccountName
      workloadPrincipalId: regionalIdentities[index].outputs.workload.principalId
    }
  }
]

module platformRbac 'modules/platform-rbac.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: platformResourceGroup
    name: 'platform-rbac-${stampNamingTokens[index]}'
    params: {
      aksPrivateDnsZoneName: 'privatelink.${stampLocation}.azmk8s.io'
      applicationInsightsName: platform.outputs.applicationInsightsName
      assignFoundryProjectLogReader: true
      containerRegistryName: platform.outputs.containerRegistryName
      controlPlanePrincipalId: regionalIdentities[index].outputs.controlPlane.principalId
      foundryProjectPrincipalId: regionalAiProjects[index].outputs.projectPrincipalId
      kubeletPrincipalId: regionalIdentities[index].outputs.kubelet.principalId
    }
  }
]

module cosmosRbac 'modules/cosmos-rbac.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: platformResourceGroup
    name: 'cosmos-rbac-${stampNamingTokens[index]}'
    params: {
      cosmosAccountName: platform.outputs.cosmosAccountName
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
      controlPlaneIdentity: regionalIdentities[index].outputs.controlPlane
      cosmosAccountId: platform.outputs.cosmosAccountId
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
      // cosmosRbac
      platformRbac
    ]
  }
]

var redisReplicationGroupNickname = take('zava-contact-center-${normalizedEnvironmentName}', 64)
var redisReplicaNames = [for (namingToken, index) in stampNamingTokens: take(replace('rediszavacc${take(namingToken, 8)}', '-', ''), 60)]
var redisDatabaseIds = [
  for (_, index) in stampLocations: '${regionalResourceGroups[index].id}/providers/Microsoft.Cache/redisEnterprise/${redisReplicaNames[index]}/databases/default'
]
@batchSize(1)
module redisGeoReplication 'modules/redis-geo-replication.bicep' = [
  for (stampLocation, index) in stampLocations: {
    scope: regionalResourceGroups[index]
    name: 'redis-geo-${stampNamingTokens[index]}'
    dependsOn:[
      regionalResourceGroups
    ]
    params: {
      privateDnsZoneId: platform.outputs.privateDnsZoneIds.redis
      privateEndpointSubnetId: regionalStamps[index].outputs.privateEndpointSubnetId
      workloadIdentityPrincipalId: regionalIdentities[index].outputs.workload.principalId
      groupNickname: redisReplicationGroupNickname
      linkedDatabaseIds: (index == 0) ? [
        '${regionalResourceGroups[index].id}/providers/Microsoft.Cache/redisEnterprise/${redisReplicaNames[index]}/databases/default'
      ] : redisDatabaseIds
      redisSkuName: redisSkuName
      redisClusterName: redisReplicaNames[index]
      tags: union(baseTags, {
        resourceGroup: regionalResourceGroups[index].name
        region: stampLocation
      })
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
output AZURE_SPEECH_ENDPOINT string = speechToTextEndpoint
output AZURE_SPEECH_TO_TEXT_ENDPOINT string = speechToTextEndpoint
output AZURE_TEXT_TO_SPEECH_ENDPOINT string = textToSpeechEndpoint
output AZURE_VOICELIVE_ENDPOINT string = voiceLiveEndpoint
output AZURE_VOICE_LIVE_ENDPOINT string = voiceLiveEndpoint
output AZURE_AI_ACCOUNT_RESOURCE_GROUP string = (useExistingAiProject)
  ? platformResourceGroup.name
  : regionalResourceGroups[0].name
output AZURE_AI_PROJECTS_JSON array = [
  for (stampLocation, index) in stampLocations: {
    accountId: regionalAiProjects[index].outputs.accountId
    accountName: regionalAiProjects[index].outputs.aiServicesAccountName
    location: stampLocation
    openAiEndpoint: regionalAiProjects[index].outputs.AZURE_OPENAI_ENDPOINT
    privateEndpointId: aiServicesPrivateEndpoints[index].outputs.id
    projectEndpoint: regionalAiProjects[index].outputs.AZURE_AI_PROJECT_ENDPOINT
    projectId: regionalAiProjects[index].outputs.projectId
    projectName: regionalAiProjects[index].outputs.projectName
    resourceGroup: (useExistingAiProject && index == 0)
      ? platformResourceGroup.name
      : regionalResourceGroups[index].name
    speechToTextEndpoint: regionalAiProjects[index].outputs.speechToTextEndpoint
    textToSpeechEndpoint: regionalAiProjects[index].outputs.textToSpeechEndpoint
    voiceLiveEndpoint: regionalAiProjects[index].outputs.voiceLiveEndpoint
  }
]
output APPLICATIONINSIGHTS_CONNECTION_STRING string = platform.outputs.applicationInsightsConnectionString
output APPLICATIONINSIGHTS_RESOURCE_ID string = platform.outputs.applicationInsightsId
output AZURE_MONITOR_WORKSPACE_ID string = platform.outputs.azureMonitorWorkspaceId
output AZURE_MONITOR_WORKSPACE_NAME string = platform.outputs.azureMonitorWorkspaceName
output AZURE_MANAGED_GRAFANA_ID string = platform.outputs.managedGrafanaId
output AZURE_MANAGED_GRAFANA_NAME string = platform.outputs.managedGrafanaName
output AZURE_MANAGED_GRAFANA_ENDPOINT string = platform.outputs.managedGrafanaEndpoint
output AZURE_AI_PROJECT_ACR_CONNECTION_NAME string = regionalAiProjects[0].outputs.dependentResources.registry.connectionName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = platform.outputs.containerRegistryEndpoint
output AZURE_CONTAINER_REGISTRY_RESOURCE_ID string = platform.outputs.containerRegistryId
output AZURE_COSMOS_DB_ACCOUNT_NAME string = platform.outputs.cosmosAccountName
output AZURE_COSMOS_DB_ENDPOINT string = platform.outputs.cosmosEndpoint
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
    id: redisGeoReplication[index].outputs.redisClusterId
    location: stampLocation
    name: redisGeoReplication[index].outputs.redisClusterName
    resourceGroup: regionalResourceGroups[index].name
  }
]
output AZURE_COMMUNICATION_SERVICES_NAME string = platform.outputs.communicationServicesName
output AZURE_COMMUNICATION_SERVICES_IMMUTABLE_RESOURCE_ID string = platform.outputs.communicationServicesImmutableResourceId

output AZURE_AI_SEARCH_CONNECTION_NAME string = regionalAiProjects[0].outputs.dependentResources.search.connectionName
output AZURE_AI_SEARCH_SERVICE_NAME string = regionalAiProjects[0].outputs.dependentResources.search.serviceName
output AZURE_STORAGE_CONNECTION_NAME string = regionalAiProjects[0].outputs.dependentResources.storage.connectionName
output AZURE_STORAGE_ACCOUNT_NAME string = regionalAiProjects[0].outputs.dependentResources.storage.accountName
output BING_GROUNDING_CONNECTION_NAME string = regionalAiProjects[0].outputs.dependentResources.bing_grounding.connectionName
output BING_GROUNDING_RESOURCE_NAME string = regionalAiProjects[0].outputs.dependentResources.bing_grounding.name
output BING_GROUNDING_CONNECTION_ID string = regionalAiProjects[0].outputs.dependentResources.bing_grounding.connectionId
output BING_CUSTOM_GROUNDING_CONNECTION_NAME string = regionalAiProjects[0].outputs.dependentResources.bing_custom_grounding.connectionName
output BING_CUSTOM_GROUNDING_NAME string = regionalAiProjects[0].outputs.dependentResources.bing_custom_grounding.name
output BING_CUSTOM_GROUNDING_CONNECTION_ID string = regionalAiProjects[0].outputs.dependentResources.bing_custom_grounding.connectionId
output AI_PROJECT_CONNECTION_IDS_JSON string = string(regionalAiProjects[0].outputs.connectionIds)
