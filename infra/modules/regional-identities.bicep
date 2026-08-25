targetScope = 'resourceGroup'

@description('Azure region for the managed identities.')
param location string = resourceGroup().location

@description('Naming token for the regional stamp.')
param namingToken string

@description('Tags applied to the managed identities.')
param tags object = {}

resource controlPlaneIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-aks-control-${namingToken}'
  location: location
  tags: tags
}

resource kubeletIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-aks-kubelet-${namingToken}'
  location: location
  tags: tags
}

resource workloadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-contact-center-${namingToken}'
  location: location
  tags: tags
}

output controlPlane object = {
  id: controlPlaneIdentity.id
  name: controlPlaneIdentity.name
  clientId: controlPlaneIdentity.properties.clientId
  principalId: controlPlaneIdentity.properties.principalId
}

output kubelet object = {
  id: kubeletIdentity.id
  name: kubeletIdentity.name
  clientId: kubeletIdentity.properties.clientId
  principalId: kubeletIdentity.properties.principalId
}

output workload object = {
  id: workloadIdentity.id
  name: workloadIdentity.name
  clientId: workloadIdentity.properties.clientId
  principalId: workloadIdentity.properties.principalId
}
