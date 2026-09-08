targetScope = 'resourceGroup'

@description('Deploy a new regional AI project or reference an existing project.')
param useExistingAiProject bool = false

@description('Azure region for the AI Services account and project.')
param location string = resourceGroup().location

@description('AZD environment name with a regional suffix for globally unique resources.')
param environmentName string

@description('Name of the Microsoft Foundry project.')
param aiFoundryProjectName string

@description('Optional existing AI Services account name.')
param existingAiAccountName string = ''

@description('Model deployments to create on the AI Services account.')
param deployments array = []

@description('Connections to provision on the Foundry project.')
param connections array = []

@secure()
@description('Map of connection names to credential objects.')
param connectionCredentials object = {}

@description('Dependent resources to provision and connect to the project.')
param additionalDependentResources array = []

@description('Enable monitoring for a newly provisioned project.')
param enableMonitoring bool = true

@description('Enable hosted agents for a newly provisioned project.')
param enableHostedAgents bool = false

@description('Enable the Foundry capability host for a newly provisioned project.')
param enableCapabilityHost bool = true

@description('Existing container registry resource ID.')
param existingContainerRegistryResourceId string = ''

@description('Existing container registry login server.')
param existingContainerRegistryEndpoint string = ''

@description('Existing ACR connection name on an existing Foundry project.')
param existingAcrConnectionName string = ''

@secure()
@description('Existing Application Insights connection string.')
param existingApplicationInsightsConnectionString string = ''

@description('Existing Application Insights resource ID.')
param existingApplicationInsightsResourceId string = ''

@description('Existing Application Insights connection name on an existing Foundry project.')
param existingAppInsightsConnectionName string = ''

@description('ID of the user or application assigned project roles.')
param principalId string

@description('Principal type of the deployment principal.')
param principalType string

@allowed([
  'Enabled'
  'Disabled'
])
@description('Public network access mode for a newly created AI Services account.')
param publicNetworkAccess string = 'Disabled'

@allowed([
  'Allow'
  'Deny'
])
@description('Default network ACL action for a newly created AI Services account.')
param networkAclsDefaultAction string = 'Deny'

@description('Tags applied to newly provisioned resources.')
param tags object = {}

module provisionedProject '../core/ai/ai-project.bicep' = if (!useExistingAiProject) {
  name: 'provisioned-ai-project'
  params: {
    additionalDependentResources: additionalDependentResources
    aiFoundryProjectName: aiFoundryProjectName
    connectionCredentials: connectionCredentials
    connections: connections
    deployments: deployments
    enableCapabilityHost: enableCapabilityHost
    enableHostedAgents: enableHostedAgents
    enableMonitoring: enableMonitoring
    environmentName: environmentName
    existingAcrConnectionName: existingAcrConnectionName
    existingAiAccountName: existingAiAccountName
    existingApplicationInsightsConnectionString: existingApplicationInsightsConnectionString
    existingApplicationInsightsResourceId: existingApplicationInsightsResourceId
    existingAppInsightsConnectionName: existingAppInsightsConnectionName
    existingContainerRegistryEndpoint: existingContainerRegistryEndpoint
    existingContainerRegistryResourceId: existingContainerRegistryResourceId
    location: location
    networkAclsDefaultAction: networkAclsDefaultAction
    principalId: principalId
    principalType: principalType
    publicNetworkAccess: publicNetworkAccess
    tags: tags
  }
}

module existingProject '../core/ai/existing-ai-project.bicep' = if (useExistingAiProject) {
  name: 'existing-ai-project'
  params: {
    aiFoundryProjectName: aiFoundryProjectName
    aiServicesAccountName: existingAiAccountName
    connectionCredentials: connectionCredentials
    connections: connections
    deployments: deployments
    existingAcrConnectionName: existingAcrConnectionName
    existingApplicationInsightsConnectionString: existingApplicationInsightsConnectionString
    existingApplicationInsightsResourceId: existingApplicationInsightsResourceId
    existingContainerRegistryEndpoint: existingContainerRegistryEndpoint
  }
}

output AZURE_AI_PROJECT_ENDPOINT string = useExistingAiProject
  ? existingProject!.outputs.AZURE_AI_PROJECT_ENDPOINT
  : provisionedProject!.outputs.AZURE_AI_PROJECT_ENDPOINT
output AZURE_OPENAI_ENDPOINT string = useExistingAiProject
  ? existingProject!.outputs.AZURE_OPENAI_ENDPOINT
  : provisionedProject!.outputs.AZURE_OPENAI_ENDPOINT
output accountId string = useExistingAiProject
  ? existingProject!.outputs.accountId
  : provisionedProject!.outputs.accountId
output aiServicesAccountName string = useExistingAiProject
  ? existingProject!.outputs.aiServicesAccountName
  : provisionedProject!.outputs.aiServicesAccountName
output projectId string = useExistingAiProject
  ? existingProject!.outputs.projectId
  : provisionedProject!.outputs.projectId
output projectName string = useExistingAiProject
  ? existingProject!.outputs.projectName
  : provisionedProject!.outputs.projectName
output projectPrincipalId string = useExistingAiProject
  ? existingProject!.outputs.projectPrincipalId
  : provisionedProject!.outputs.projectPrincipalId
output speechToTextEndpoint string = useExistingAiProject
  ? existingProject!.outputs.speechToTextEndpoint
  : provisionedProject!.outputs.speechToTextEndpoint
output textToSpeechEndpoint string = useExistingAiProject
  ? existingProject!.outputs.textToSpeechEndpoint
  : provisionedProject!.outputs.textToSpeechEndpoint
output voiceLiveEndpoint string = useExistingAiProject
  ? existingProject!.outputs.voiceLiveEndpoint
  : provisionedProject!.outputs.voiceLiveEndpoint
output connectionIds array = useExistingAiProject
  ? existingProject!.outputs.connectionIds
  : provisionedProject!.outputs.connectionIds
output dependentResources object = useExistingAiProject
  ? existingProject!.outputs.dependentResources
  : provisionedProject!.outputs.dependentResources
