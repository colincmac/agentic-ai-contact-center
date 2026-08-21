# Teams Phone Extensibility (TPE) — Enterprise Onboarding Guide

This guide walks through the end-to-end process of connecting Azure Communication Services (ACS) with Teams Phone via Teams Phone Extensibility (TPE). It supplements the [official quickstart](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart) and [answer-calls guide](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls) with enterprise-scale automation scripts and operational details not covered in the public docs.

## Scripts

Provisioning is split into two scripts separated by **tenant boundary** and **permission scope**. Every phase is **idempotent** — re-running against an environment that already has the Entra App, Resource Account, phone number, Bot Service, ACS resource, or Event Grid subscription will reuse them.

| Script | Tenant | Required Roles | Phases |
|--------|--------|----------------|--------|
| [`setup_tpe_teams.ps1`](../../../scripts/teams-extensibility/setup_tpe_teams.ps1) | Teams (M365) | Global Admin **or** (Cloud Application Administrator + Teams Communications Administrator + User Administrator) | 1. Entra App + Service Principal + secret + admin consent → 2. Resource Account → 3. License + Phone |
| [`setup_tpe_azure.ps1`](../../../scripts/teams-extensibility/setup_tpe_azure.ps1) | Azure | Contributor on the ACS resource (and on the resource group for Bot creation) | 1. Bot Service → 2. ACS TPE Authorization → 3. Event Grid IncomingCall subscription |
| [`cleanup_tpe.ps1`](../../../scripts/teams-extensibility/cleanup_tpe.ps1) | Both | Same as above | Reverses the steps above (with confirmation) |

The Teams script runs **first** and writes `tpe-teams-output.json` (Entra App Client ID, RA Object ID, etc.). The Azure script consumes it.

For environments where the **same admin** manages both tenants, use the orchestrator [`setup_tpe.ps1`](../../../scripts/teams-extensibility/setup_tpe.ps1) which calls both in sequence.

### Brownfield / pre-existing resources

The most common enterprise scenario is that the **ACS resource already exists** and the **Teams Resource Account with phone number already exists**. The scripts handle this:

| Pre-existing | Behaviour |
|--------------|-----------|
| ACS resource | Auto-discovered via `az resource show` from `acsName` + `resourceGroupName`. Not created. |
| Entra App | Looked up by `existingEntraAppClientId` (preferred) or by display name. Existing app's `RequiredResourceAccess` is patched only if `TeamsExtension.ManageCalls` is missing. |
| Service Principal | Created only if missing for the app. |
| Resource Account | Detected via `Get-CsOnlineApplicationInstance`. Re-bound to the configured ACS resource only when `AcsResourceId`/`ApplicationId` differs. |
| Phone number | Detected via `Get-CsPhoneNumberAssignment`. Skipped if already assigned to the same RA; throws if assigned elsewhere. |
| License | `Get-MgUserLicenseDetail` is consulted; assignment is skipped when the SKU is already present. |
| Bot Service | `az bot show` short-circuits create. |
| ACS TPE assignment | Pre-flight `GET` distinguishes 404 (create) from 401/403 (RBAC failure). PUT acts as upsert. |
| Event Grid subscription | `az eventgrid event-subscription show` short-circuits or upgrades to `update` if endpoint differs. |

### Dependency Graph

```
setup_tpe_teams.ps1 (Teams Admin)              setup_tpe_azure.ps1 (Azure Admin)
─────────────────────────────────               ─────────────────────────────────
Phase 1: Entra App + SP + secret  ────┬──────► Phase 1: Azure Bot Service
         + admin consent              │              (needs Entra App Client ID)
    │                                 │
    ▼                                 │
Phase 2: Teams Resource Account ──────┤
    │                                 │
    ▼                                 ├──────► Phase 2: ACS TPE Authorization
Phase 3: License + Phone Number       │              (needs RA Object ID)
                                      │
                                      └──────► Phase 3: Event Grid IncomingCall
                                                     subscription on ACS
                                                     (filtered to RA Object ID)
```

### Quick Start

```powershell
# Option A: Separate admins (recommended for enterprise)
# --- Teams Admin runs: ---
.\setup_tpe_teams.ps1 -ConfigFile .\tpe-config.sample.json

# --- Azure Admin runs (after receiving tpe-teams-output.json): ---
.\setup_tpe_azure.ps1 -ConfigFile .\tpe-config.sample.json -TeamsOutputFile .\tpe-teams-output.json

# Option B: Single admin with access to both tenants
.\setup_tpe.ps1 -ConfigFile .\tpe-config.sample.json
```

## Two-Tenant Architecture

TPE requires resources across **two separate tenants**:

| Tenant | Purpose | Required Roles |
|--------|---------|----------------|
| **Azure Tenant** | Hosts Azure subscription with ACS resource, Bot Service, and application infrastructure | Contributor on the resource group |
| **Teams Tenant** | Hosts Microsoft 365 / Teams Phone with resource accounts, phone numbers, and licensing | Global Admin **or** (Cloud Application Administrator + Teams Communications Administrator + User Administrator) |

> **Important:** The ACS resource data location must match the Teams tenant location to comply with data boundary regulations. Verify via [Get organization](https://learn.microsoft.com/en-us/graph/api/organization-get).

```
┌─────────────────────────────────┐       ┌─────────────────────────────────────┐
│         Azure Tenant            │       │          Teams Tenant               │
│                                 │       │                                     │
│  ┌───────────────────────────┐  │       │  ┌─────────────────────────────┐    │
│  │ Azure Communication       │  │       │  │ Entra App Registration      │    │
│  │ Services (ACS)            │◄─┼───────┼──│  - TeamsExtension.          │    │
│  │  - TPE assignment API     │  │       │  │    ManageCalls permission   │    │
│  └───────────────────────────┘  │       │  └─────────────────────────────┘    │
│                                 │       │                 │                   │
│  ┌───────────────────────────┐  │       │  ┌─────────────────────────────┐    │
│  │ Azure Bot Service         │  │       │  │ Teams Resource Account      │    │
│  │  - AppId = Entra App      │  │       │  │  - Linked to Entra App      │    │
│  │    ClientId               │  │       │  │  - Phone number assigned    │    │
│  │  - TenantId = Teams       │  │       │  │  - Licensed (RA + Calling)  │    │
│  │    Tenant ID              │  │       │  │  - AcsResourceId set        │    │
│  └───────────────────────────┘  │       │  └─────────────────────────────┘    │
│                                 │       │                                     │
└─────────────────────────────────┘       └─────────────────────────────────────┘
```

## Prerequisites

### Modules

| Module | Version | Purpose |
|--------|---------|---------|
| `MicrosoftTeams` | ≥ 7.5.0 | Teams resource account provisioning |
| `Microsoft.Entra` | ≥ 1.2.0 | Entra app registration in Teams tenant |
| `Microsoft.Graph.Users.Actions` | latest | License assignment via `Set-MgUserLicense` |
| Azure CLI (`az`) | latest | Bot Service creation + ACS TPE assignment API |

### Well-Known Constants

| Constant | Value | Description |
|----------|-------|-------------|
| ACS First-Party App ID | `1fd5118e-2576-4263-8130-9503064c837a` | `https://auth.msft.communication.azure.com` |
| TeamsExtension.ManageCalls Permission ID | `9ed60762-c537-4e50-8984-4b1db3d922ce` | Required API permission for TPE |
| Teams Phone Resource Account SKU ID | `440eaaa8-b3e0-484b-a8be-62870b9ba70a` | License SKU for resource accounts |
| TPE Assignment API Version | `2025-06-30` | Current API version for ACS TPE endpoints |

---

## Step-by-Step Provisioning

> **Execution order:** The Teams script runs first (all 3 phases). Once Phase 1 completes, the Azure admin can start in parallel. Azure Phase 2 requires the RA Object ID from Teams Phase 2.

### Teams Script — Phase 1: Create Entra App Registration

**Script:** `setup_tpe_teams.ps1` Phase 1 | **Persona:** Teams Tenant Global Admin or Application Administrator

This is the **root dependency** — both the Azure Bot Service and the Teams Resource Account need the Entra App Client ID. Create an app registration **in the Teams tenant** with the `TeamsExtension.ManageCalls` permission for ACS.

```powershell
# Install the Entra module
Install-Module -Name Microsoft.Entra -RequiredVersion 1.2.0 -Repository PSGallery -Scope CurrentUser -Force -AllowClobber

# Connect to the Teams tenant
Connect-Entra -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All" -TenantId $teamsTenantId

# Define the required ACS permission
$requiredResourceAccess = @(
    @{
        resourceAppId  = "1fd5118e-2576-4263-8130-9503064c837a"  # ACS first-party app
        resourceAccess = @(
            @{
                id   = "9ed60762-c537-4e50-8984-4b1db3d922ce"    # TeamsExtension.ManageCalls
                type = "Scope"
            }
        )
    }
)

# Create the Entra app registration
$entraApp = New-EntraApplication -DisplayName $entraAppRegistrationName -RequiredResourceAccess $requiredResourceAccess

# Record the Application (Client) ID — this is used as the Bot ApplicationId and RA ApplicationId
Write-Host "Entra App Client ID: $($entraApp.AppId)"
```

> **Troubleshooting:** If consent is blocked, see [consent troubleshooting](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting#consent-blocked-due-to-microsoft-entra-app-permission).

After this phase, **share the Entra App Client ID with the Azure admin** so they can create the Bot Service in parallel with the remaining Teams phases.

---

### Teams Script — Phase 2: Provision Resource Account

**Script:** `setup_tpe_teams.ps1` Phase 2 | **Persona:** Teams Admin (Skype for Business Administrator + User Administrator)

This creates the Teams Resource Account, links it to the Entra app, and associates it with the ACS resource.

```powershell
# Connect to Microsoft Teams and Microsoft Graph
Connect-MicrosoftTeams -TenantId $teamsTenantId
Connect-Graph -Scopes User.ReadWrite.All, Organization.Read.All

# 3a. Create the resource account
#     ApplicationId = Entra App Client ID (NOT a first-party Teams app ID)
$teamsResourceAccount = New-CsOnlineApplicationInstance `
    -UserPrincipalName $teamsResourceAccountUpn `
    -ApplicationId $entraApp.AppId `
    -DisplayName $teamsResourceAccountDescription

# 3b. Link the resource account to ACS
Set-CsOnlineApplicationInstance `
    -Identity $teamsResourceAccount.UserPrincipalName `
    -ApplicationId $entraApp.AppId `
    -AcsResourceId $azureCommunicationServiceGlobalId

# 3c. Sync to the Agent Provisioning Service
Sync-CsOnlineApplicationInstance `
    -ObjectId $teamsResourceAccount.ObjectId `
    -ApplicationId $entraApp.AppId
```

> **Critical:** Do **not** use Teams first-party Application IDs (Auto Attendant, Call Queue, etc.) as the `ApplicationId`. You must use your own Entra app's Client ID.

---

### Teams Script — Phase 3: License and Assign Phone Number

**Script:** `setup_tpe_teams.ps1` Phase 3 | **Persona:** Teams Admin

The resource account must be licensed and have a phone number before it can receive calls. There is a propagation delay (15–60 seconds) after creating the resource account before it appears in Entra ID.

```powershell
# Wait for the resource account to propagate to Entra ID
do {
    Write-Host "Waiting for resource account to appear in Entra ID..." -ForegroundColor Cyan
    Start-Sleep 15
    $resourceAccountObject = Get-MgUser -UserId $teamsResourceAccount.UserPrincipalName
} until ($resourceAccountObject.UserPrincipalName -eq $teamsResourceAccount.UserPrincipalName)

# 4a. Set usage location (required for license assignment)
Update-MgUser -UserId $teamsResourceAccount.UserPrincipalName -UsageLocation "US"
Start-Sleep 15

# 4b. Assign the Teams Phone Resource Account license
$teamsRATeamsPhoneSkuId = "440eaaa8-b3e0-484b-a8be-62870b9ba70a"
do {
    $error.Clear()
    Start-Sleep 15
    Set-MgUserLicense -UserId $teamsResourceAccount.UserPrincipalName `
        -AddLicenses @(@{SkuId = $teamsRATeamsPhoneSkuId}) `
        -RemoveLicenses @()
} until (!$error)

# 4c. Assign the phone number
Set-CsPhoneNumberAssignment `
    -Identity $teamsResourceAccount.UserPrincipalName `
    -PhoneNumber $teamsPhoneNumber `
    -PhoneNumberType CallingPlan    # or DirectRouting / OperatorConnect
```

**Phone number type considerations:**

| Type | Requirements |
|------|-------------|
| **Calling Plan** | Resource account must have a Calling Plan service number and the RA license |
| **Direct Routing** | Tenant must have a verified SBC, DR phone number, and Voice Routing Policy assigned to the RA |
| **Operator Connect** | Number must be provisioned by an approved OC provider that supports outbound PSTN for voice apps |

> **Outbound calls:** If the resource account needs to make outbound PSTN calls, also assign a [Microsoft Teams Calling Plan](https://learn.microsoft.com/en-us/microsoftteams/calling-plans-for-office-365) license.

---

After this phase completes, share the `tpe-teams-output.json` file (or the RA Object ID) with the Azure admin for Phase 2 of the Azure script.

---

### Azure Script — Phase 1: Create Bot Service

**Script:** `setup_tpe_azure.ps1` Phase 1 | **Persona:** Azure Subscription Contributor

The Bot Service links your Entra App (from the Teams tenant) to Azure. The `ApplicationId` is the Client ID of the Entra App Registration, and the `TenantId` is the **Teams tenant** ID. This phase can run **in parallel** with Teams Phases 2 and 3 once the Entra App Client ID is available.

```powershell
# Sign in to the Azure tenant
az login --tenant $azureTenantId

# Register the Bot Service resource provider (first-time only)
Register-AzResourceProvider -ProviderNamespace Microsoft.BotService

# Create the bot — ApplicationId is your Entra App Client ID, TenantId is the TEAMS tenant
az bot create `
    --resource-group $azureResourceGroupName `
    --name $azureBotServiceName `
    --app-type "MultiTenant" `
    --appid $entraAppClientId `
    --tenant-id $teamsTenantId `
    --sku S1 `
    --location "global" `
    --subscription $azureSubscriptionId
```

> **Note:** The webhook URL can be any valid URL (e.g. `https://yourcompany.com`). Microsoft plans to remove this dependency in future.

---

### Azure Script — Phase 2: Authorize ACS to Accept Calls (TPE Assignment)

**Script:** `setup_tpe_azure.ps1` Phase 2 | **Persona:** Azure Subscription Contributor (must be logged in to Azure CLI)

This is the step the public docs describe as "Configure your Communication Services resource to accept calls for the Teams resource account." It calls the ACS TPE Assignment API to create an authorization linking the Teams resource account to the ACS resource.

Use the provided [`azure_acs_tpe_auth.ps1`](../../../scripts/teams-extensibility/azure_acs_tpe_auth.ps1) script:

```powershell
# Ensure you're logged into the Azure tenant
az login --tenant $azureTenantId

# Authorize ACS to accept calls for the Teams resource account
.\eng\scripts\azure_acs_tpe_auth.ps1 `
    -AzureCommunicationServicesName $azureCommunicationServicesName `
    -TeamsTenantId $teamsTenantId `
    -TeamsResourceAccountObjectId $teamsResourceAccount.ObjectId `
    -PrincipalType "teamsResourceAccount" `
    -Verbose
```

**What the script does:**
1. Constructs the ACS TPE assignment API URL:
   ```
   PUT https://{acsName}.communication.azure.com/access/teamsExtension/tenants/{teamsTenantId}/assignments/{raObjectId}?api-version=2025-06-30
   ```
2. Sends a JSON body with `principalType: "teamsResourceAccount"` and an optional `clientIds` array
3. Uses `az rest` with `--resource https://communication.azure.com` for Entra-based authentication
4. Writes the JSON body to a temp file to avoid Windows shell escaping issues with `az.cmd`

**Verify the assignment:**
```powershell
az rest `
    --method GET `
    --url "https://$azureCommunicationServicesName.communication.azure.com/access/teamsExtension/tenants/$teamsTenantId/assignments/$($teamsResourceAccount.ObjectId)?api-version=2025-06-30" `
    --resource "https://communication.azure.com"
```

**Remove the assignment (stop accepting calls):**
```powershell
az rest `
    --method DELETE `
    --url "https://$azureCommunicationServicesName.communication.azure.com/access/teamsExtension/assignments/$($teamsResourceAccount.ObjectId)?api-version=2025-06-30" `
    --resource "https://communication.azure.com"
```

---

## Verification Checklist

After completing all phases, verify the setup. Items 1–7 must all pass before testing.

| # | Check | Command / Where |
|---|-------|-----------------|
| 1 | Resource Account exists and is linked to ACS | `Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn` — confirm `AcsResourceId` and `ApplicationId` match the configured values. |
| 2 | Phone number is assigned to the RA | `Get-CsPhoneNumberAssignment -TelephoneNumber $teamsPhoneNumber` — confirm `AssignedPstnTargetId` is the RA. |
| 3 | RA has the Teams Phone Resource Account license | `Get-MgUserLicenseDetail -UserId $teamsResourceAccountUpn` |
| 4 | Entra App has admin consent for `TeamsExtension.ManageCalls` | Azure portal → Entra ID → App registrations → your app → API permissions → status column reads `Granted for <tenant>`. |
| 5 | ACS TPE assignment exists | `az rest --method GET --url "https://{acsName}.communication.azure.com/access/teamsExtension/tenants/{teamsTenantId}/assignments/{raObjectId}?api-version=2025-06-30" --resource "https://communication.azure.com"` |
| 6 | Bot Service exists with the correct AppId / TenantId | `az bot show --name $botServiceName --resource-group $rg --query '{appId:properties.msaAppId, tenantId:properties.msaAppTenantId}'` |
| 7 | Event Grid subscription is `Enabled` and validation handshake completed | Azure portal → ACS resource → Events → confirm `tpe-incoming-call` subscription shows `Enabled` and the `Microsoft.Communication.IncomingCall` event type. The webhook target must have responded to the validation request. |
| 8 | End-to-end test call | Place a PSTN call to the assigned phone number. Your callback handler should log an `IncomingCall` event whose `to.rawId` ends in the RA Object ID. |

## Teardown

The [`cleanup_tpe.ps1`](../../../scripts/teams-extensibility/cleanup_tpe.ps1) script reverses the provisioning, in safe order, with `-Confirm` prompts. By default it preserves the Entra App and the Resource Account user object — opt in to delete them.

```powershell
# Tear down everything except the Entra App and the RA user object
.\eng\scripts\cleanup_tpe.ps1 -ConfigFile .\eng\scripts\tpe-config.json

# Tear down everything including the Entra App and RA user object
.\eng\scripts\cleanup_tpe.ps1 -ConfigFile .\eng\scripts\tpe-config.json -RemoveResourceAccount -RemoveEntraApp
```

| Step | Removed by default | Notes |
|------|--------------------|-------|
| Event Grid subscription | yes | |
| ACS TPE assignment | yes | |
| Bot Service | yes | |
| Phone number assignment | yes | Number remains in tenant inventory. |
| RA license | yes | |
| Resource Account | no | Use `-RemoveResourceAccount`. |
| Entra App + Service Principal | no | Use `-RemoveEntraApp`. |
| ACS resource | never | Manage outside this script. |

---

## Provisioning at Scale

For enterprise customers managing many resource accounts, the Entra App and Bot Service are created **once**. The per-account steps loop the existing scripts with a per-RA config override. Because both scripts are idempotent, re-runs are safe.

**Teams Admin** — drive the Teams script per RA:

```powershell
$rows = Import-Csv "resource-accounts.csv"   # columns: UPN, DisplayName, PhoneNumber, PhoneNumberType
$baseConfig = Get-Content .\eng\scripts\tpe-config.json -Raw | ConvertFrom-Json
$results = @()

foreach ($row in $rows) {
    $perRa = $baseConfig.PSObject.Copy()
    $perRa.teams.resourceAccountUpn         = $row.UPN
    $perRa.teams.resourceAccountDisplayName = $row.DisplayName
    $perRa.teams.phoneNumber                = $row.PhoneNumber
    $perRa.teams.phoneNumberType            = $row.PhoneNumberType

    $tmp = New-TemporaryFile
    $perRa | ConvertTo-Json -Depth 10 | Set-Content $tmp

    $output = Join-Path $env:TEMP "tpe-teams-output-$($row.UPN -replace '[^\w]','_').json"
    .\eng\scripts\setup_tpe_teams.ps1 -ConfigFile $tmp -OutputFile $output `
        -ExistingEntraAppClientId $baseConfig.teams.existingEntraAppClientId

    $results += Get-Content $output -Raw | ConvertFrom-Json
}

$results | Select-Object teamsResourceAccountUpn, teamsResourceAccountObjectId, phoneNumber |
    Export-Csv "ra-objectids.csv" -NoTypeInformation
```

**Azure Admin** — loop the Azure script per RA Object ID:

```powershell
$accounts = Import-Csv "ra-objectids.csv"
foreach ($ra in $accounts) {
    .\eng\scripts\setup_tpe_azure.ps1 `
        -ConfigFile .\eng\scripts\tpe-config.json `
        -TeamsResourceAccountObjectId $ra.teamsResourceAccountObjectId `
        -SkipBotCreation `   # Bot is shared across all RAs
        -Phases Phase2,Phase3
}
```

---

## Key Identity Relationships

Understanding which ID goes where is the most common source of confusion:

| Field | Value | Where It's Used |
|-------|-------|-----------------|
| **Entra App Client ID** | `AppId` from `New-EntraApplication` | Bot Service `--appid`, RA `ApplicationId`, `Set-CsOnlineApplicationInstance -ApplicationId` |
| **ACS Resource Global ID** | Immutable resource ID from Azure portal | `Set-CsOnlineApplicationInstance -AcsResourceId`, TPE assignment API URL |
| **Teams Tenant ID** | Directory ID of the M365/Teams tenant | Bot Service `--tenant-id`, `Connect-MicrosoftTeams -TenantId`, TPE assignment API URL |
| **RA Object ID** | `ObjectId` from `New-CsOnlineApplicationInstance` | `Sync-CsOnlineApplicationInstance -ObjectId`, TPE assignment API URL path |
| **ACS Resource Name** | DNS-friendly name of the ACS resource | TPE assignment API hostname: `{name}.communication.azure.com` |

> **Finding the ACS Resource Global ID:** In the Azure portal, navigate to your ACS resource → Properties → Resource ID (immutable). Or use [Get an immutable resource identifier](https://learn.microsoft.com/en-us/azure/communication-services/concepts/troubleshooting-info#get-an-immutable-resource-id).

---

## Gaps in Public Documentation

The following items are **not fully documented** in the official Microsoft Learn articles and are addressed by this guide and the scripts in `scripts/teams-extensibility/`:

1. **ACS TPE Assignment API body format** — The public docs show a minimal `{ "principalType": "teamsResourceAccount" }` body. The actual Entra-auth variant requires wrapping in a `{ "request": { ... } }` envelope with a `clientIds` array (can be empty). See `azure_acs_tpe_auth.ps1`.

2. **Windows shell escaping** — `az rest --body` on Windows strips embedded double-quotes from inline JSON. The `azure_acs_tpe_auth.ps1` script works around this by writing to a temp file and using `--body @filepath` syntax.

3. **Propagation delays** — After `New-CsOnlineApplicationInstance`, the resource account takes 15–60 seconds to appear in Entra ID. License assignment may also require retries. The public docs don't mention these delays.

4. **License assignment automation** — The public docs reference the M365 Admin Center UI. For automation, use `Set-MgUserLicense` with the RA SKU ID `440eaaa8-b3e0-484b-a8be-62870b9ba70a`, preceded by `Update-MgUser -UsageLocation`. Calling Plan numbers additionally require a Calling Plan / Communications Credits SKU on the RA — supply via `teams.additionalLicenseSkuIds` in the config.

5. **ACS endpoint region suffix** — The TPE assignment API endpoint varies by region (e.g., `unitedstates.communication.azure.com` vs `communication.azure.com`). The `azure_acs_tpe_auth.ps1` script uses the base `communication.azure.com` which routes correctly.

6. **Sync is mandatory** — `Sync-CsOnlineApplicationInstance` must be called after linking the RA to ACS. Without this, call routing won't work even if the assignment API succeeds. The script always calls it after a successful bind, and it is safe to re-run after later license changes.

7. **Admin consent is required** — Declaring `RequiredResourceAccess` only writes the manifest. Until an admin clicks _Grant admin consent_ (or the script calls `New-EntraOauth2PermissionGrant -ConsentType AllPrincipals`), the assignment API returns opaque auth errors. The Teams troubleshooting article calls this out as the #1 failure mode.

8. **Service Principal must exist for the Entra App** — `New-EntraApplication` does not create the SP. Without an SP, you cannot grant admin consent. The script calls `New-EntraServicePrincipal` after registering the app.

9. **Event Grid subscription is required, not optional** — Without a `Microsoft.Communication.IncomingCall` subscription on the ACS resource, calls reach ACS but never reach your callback. Phase 3 of the Azure script provisions this with an advanced filter on `data.to.rawId` so a single ACS resource can serve multiple Resource Accounts cleanly.

10. **RBAC on the ACS resource** — The TPE assignment API calls require the caller to hold `Microsoft.Communication/communicationServices/teamsExtension/*` (granted by `Contributor`) on the ACS resource. The script issues a pre-flight `GET` and surfaces 401/403 with a remediation message instead of letting `az rest` print a stack trace.

11. **Triple sign-in prompt** — `Connect-Entra`, `Connect-MicrosoftTeams`, and `Connect-Graph` all sign in independently. Expect three browser pop-ups in succession when running the Teams script for the first time.

---

## References

- [Teams Phone Extensibility Overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-overview)
- [TPE Quickstart](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-quickstart)
- [Answer Teams Phone Calls from Call Automation](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/tpe/teams-phone-extensibility-answer-teams-calls)
- [TPE Troubleshooting](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/tpe/teams-phone-extensibility-troubleshooting)
- [Set-CsOnlineApplicationInstance](https://learn.microsoft.com/en-us/powershell/module/teams/set-csonlineapplicationinstance)
- [Licensing Service Plan Reference](https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference)
- [ACS First-Party App Permissions](https://github.com/maciejporebski/azure-ad-first-party-apps-permissions/blob/master/apps/Azure%20Communication%20Services.md)
