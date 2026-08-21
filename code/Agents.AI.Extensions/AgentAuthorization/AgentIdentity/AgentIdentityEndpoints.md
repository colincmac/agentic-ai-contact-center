$body = @{
  "@odata.type" = "#Microsoft.Graph.AgentIdentity"
  "displayName" = "Contact Center Agent"
  "agentIdentityBlueprintId" = "02f89f3c-8a5f-44a0-b4fc-0849f03a508e"
  "sponsors@odata.bind" = @(
    "https://graph.microsoft.com/v1.0/users/3c8db315-ff11-49d8-b844-b37b277ed5d6"
  )
  "owners@odata.bind" = @(
    "https://graph.microsoft.com/v1.0/users/3c8db315-ff11-49d8-b844-b37b277ed5d6"
  )
} | ConvertTo-Json -Depth 5

Invoke-WebRequest `
  -Method POST `
  -Uri "http://localhost:5130/identity/agentidentity" `
  -ContentType "application/json" `
  -Body $body


$bp = @{
    appId   = "02f89f3c-8a5f-44a0-b4fc-0849f03a508e"
}
Invoke-MgGraphRequest -Method POST `
        -Uri "https://graph.microsoft.com/beta/serviceprincipals/graph.agentIdentityBlueprintPrincipal" `
        -Headers @{ "OData-Version" = "4.0" } `
        -Body ($bp | ConvertTo-Json)

