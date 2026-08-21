# Connecting an existing Teams Phone Resource Account to an existing ACS resource

This is the **brownfield** path. Your ACS instance and your Teams resource account (with phone number + license) already exist; you only need to wire them together. The work splits across two tenants and there are six things that **must** be in place before a PSTN call routes through ACS to your callback.

For full background on the architecture, scripts, and gaps in the public docs, see [tpe-onboarding-guide.md](tpe-onboarding-guide.md).

## Prerequisites

### Powershell and AZ CLI requirements

```powershell

```

### Collect these IDs before you start

| Variable | Where to find it |
|----------|------------------|
| `azureTenantId` | Azure portal → Microsoft Entra ID → Overview → Tenant ID |
| `azureSubscriptionId` | `az account show --query id -o tsv` |
| `azureResourceGroupName` | The RG containing your ACS resource |
| `acsName` | DNS-friendly name of the ACS resource (e.g. `woodgrove-ai`) |
| `acsGlobalId` | ACS resource → Properties → **Immutable Resource ID** (a GUID, not the ARM resource ID) |
| `teamsTenantId` | M365 Admin Center → Settings → Org settings → Org information → Tenant ID |
| `teamsResourceAccountUpn` | The RA's UPN (e.g. `ivr@contoso.onmicrosoft.com`) |
| `teamsResourceAccountObjectId` | `Get-CsOnlineApplicationInstance -Identity <upn>` → `ObjectId` |
| `teamsPhoneNumber` | E.164 form (e.g. `+16105188952`) |
| `eventGridEndpointUrl` | Public HTTPS URL of your `IncomingCall` webhook (dev tunnel, App Service, etc.) |

Roles required:
- **Teams tenant:** Global Admin, or `Cloud Application Administrator` + `Teams Communications Administrator` + `User Administrator`.
- **Azure tenant:** `Contributor` on the **ACS resource itself** (the TPE assignment API does not honor RG-only Contributor).

Pre-flight check on the existing RA — confirm the current state and capture the Object ID:

```powershell
Connect-MicrosoftTeams -TenantId $teamsTenantId
Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn |
    Format-List UserPrincipalName, ObjectId, ApplicationId, AcsResourceId, PhoneNumber
```

You'll see one of two states:
- `ApplicationId` is a Teams first-party ID (Auto Attendant `ce933385-9390-45d1-9512-c8d228074e07`, Call Queue `11cd3e2e-fccb-42ad-ad00-878b93575e07`, etc.) → **the RA must be re-bound** to your own Entra app. TPE will not work with first-party app IDs.
- `ApplicationId` already points to a third-party app and `AcsResourceId` is null/different → re-bind to your Entra app and your ACS resource.

---

## Step 1 — Create the Entra App Registration in the Teams tenant

This is the identity the Bot Service, the RA, and the calling SDK all reference. It must live in the **Teams tenant**, not the Azure tenant.

```powershell
Install-Module -Name Microsoft.Entra -RequiredVersion 1.2.0 -Scope CurrentUser -Force
Connect-Entra -Scopes "Application.ReadWrite.All","AppRoleAssignment.ReadWrite.All","DelegatedPermissionGrant.ReadWrite.All" -TenantId $teamsTenantId

$requiredResourceAccess = @(
    @{
        resourceAppId  = "1fd5118e-2576-4263-8130-9503064c837a"  # ACS first-party app
        resourceAccess = @(@{ id = "9ed60762-c537-4e50-8984-4b1db3d922ce"; type = "Scope" })  # TeamsExtension.ManageCalls
    }
)
$entraApp = New-EntraApplication -DisplayName "IVR Agent Identity" -RequiredResourceAccess $requiredResourceAccess
$entraAppClientId = $entraApp.AppId
```

## Step 2 — Create the Service Principal for that app

`New-EntraApplication` does not create the SP. Without an SP you cannot grant admin consent, and the assignment API will reject your calls.

```powershell
$sp = New-EntraServicePrincipal -AppId $entraAppClientId
```

## Step 3 — Grant tenant-wide admin consent for `TeamsExtension.ManageCalls`

The single most common failure mode for TPE. Declaring `RequiredResourceAccess` only writes the manifest — it does **not** grant consent. Until consent is granted, the TPE assignment API returns opaque `401`/`403` errors.

```powershell
$acsSp = Get-EntraServicePrincipal -Filter "appId eq '1fd5118e-2576-4263-8130-9503064c837a'" | Select-Object -First 1
if (-not $acsSp) { $acsSp = New-EntraServicePrincipal -AppId "1fd5118e-2576-4263-8130-9503064c837a" }

New-EntraOauth2PermissionGrant `
    -ClientId $sp.Id `
    -ConsentType "AllPrincipals" `
    -ResourceId $acsSp.Id `
    -Scope "TeamsExtension.ManageCalls"
```

Verify in the Azure portal: **Entra ID → App registrations → your app → API permissions** — the status column for `TeamsExtension.ManageCalls` should read `Granted for <tenant>`.

## Step 4 — Create a client secret (and store it in Key Vault)

The Bot Service does not strictly require a secret on day 1, but you will need one for any server-side code that authenticates as this app (Call Automation, OBO calls, calling on behalf of the RA, etc.). Capture it now — it cannot be retrieved later.

```powershell
$cred = New-EntraApplicationPasswordCredential -ApplicationId $entraApp.Id -PasswordCredential @{
    DisplayName = "tpe-onboarding-$(Get-Date -Format 'yyyyMMdd')"
    EndDateTime = (Get-Date).AddMonths(12)
}

# Store it in Key Vault rather than the filesystem
$secure = ConvertTo-SecureString $cred.SecretText -AsPlainText -Force
Set-AzKeyVaultSecret -VaultName $keyVaultName -Name "tpe-entra-app-secret" -SecretValue $secure
```

## Step 5 — Re-bind the existing Resource Account to your Entra app and ACS resource

This is the change that swaps the RA off whatever AppId it currently uses (often a Teams first-party AppId from when it was created as an Auto Attendant or Call Queue) onto your TPE-enabled Entra app, and points it at your ACS resource.

```powershell
Connect-MicrosoftTeams -TenantId $teamsTenantId

# Re-bind to your Entra app + ACS resource. Idempotent — safe to re-run.
Set-CsOnlineApplicationInstance `
    -Identity $teamsResourceAccountUpn `
    -ApplicationId $entraAppClientId `
    -AcsResourceId $acsGlobalId

# Sync to the Agent Provisioning Service. MANDATORY — call routing won't work without this,
# even if every other step succeeded.
$ra = Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn
Sync-CsOnlineApplicationInstance -ObjectId $ra.ObjectId -ApplicationId $entraAppClientId

$teamsResourceAccountObjectId = $ra.ObjectId
```

> Re-binding does not affect the assigned phone number or the existing license. The number stays attached to the RA UPN.

## Step 6 — Create the Azure Bot Service (Azure tenant)

The Bot Service is what connects your Entra App (which lives in the **Teams** tenant) into Azure as a callable identity. The `--appid` is the Entra App Client ID; the `--tenant-id` is the **Teams tenant** ID.

```powershell
az login --tenant $azureTenantId
az account set --subscription $azureSubscriptionId
az provider register --namespace Microsoft.BotService --wait   # first-time only

az bot create `
    --resource-group $azureResourceGroupName `
    --name "wdg-ivr-agent" `
    --app-type "MultiTenant" `
    --appid $entraAppClientId `
    --tenant-id $teamsTenantId `
    --sku S1 `
    --location "global" `
    --endpoint "https://yourcompany.example/api/messages" `
    --subscription $azureSubscriptionId
```

The endpoint URL is currently required by the Bot Service create API but is not used for TPE call routing. Any valid HTTPS URL works.

## Step 7 — Authorize the ACS resource to accept calls for the RA (TPE assignment)

This is the step that the public docs describe as "Configure your Communication Services resource to accept calls for the Teams resource account." It calls the **Microsoft Teams Extension access assignments API** on the ACS data plane and creates an assignment record linking the RA Object ID into the ACS resource's authorization table.

The body shape required by the Entra-auth variant of the API differs from the public docs example — it must be wrapped in a `request` envelope with a `clientIds` array (empty `[]` is allowed). Use [`azure_acs_tpe_auth.ps1`](../../../scripts/teams-extensibility/azure_acs_tpe_auth.ps1) which handles the envelope and the Windows shell escaping issue with `az rest --body`:

```powershell
.\eng\scripts\azure_acs_tpe_auth.ps1 `
    -AzureCommunicationServicesName $acsName `
    -TeamsTenantId $teamsTenantId `
    -TeamsResourceAccountObjectId $teamsResourceAccountObjectId `
    -PrincipalType "teamsResourceAccount" `
    -Verbose
```

Verify:

```powershell
$verifyUrl = "https://$acsName.communication.azure.com/access/teamsExtension/tenants/$teamsTenantId/assignments/${teamsResourceAccountObjectId}?api-version=2025-06-30"
az rest --method GET --url $verifyUrl --resource "https://communication.azure.com"
```

Expected: a `200` with `principalType: "teamsResourceAccount"` and your RA Object ID.

If you get a `401`/`403` here, it's almost always one of:
- Admin consent (Step 3) was not granted.
- The signed-in `az` principal lacks `Contributor` on the **ACS resource itself** (RG-level Contributor is not sufficient).
- You signed `az` into the wrong tenant.

## Step 8 — Create the Event Grid `IncomingCall` subscription on the ACS resource

Without this, calls reach ACS but never reach your callback. The Event Grid subscription is what delivers `Microsoft.Communication.IncomingCall` events to your webhook so your code can call `AnswerCallAsync`.

```powershell
$acsResourceId = az resource show `
    --resource-type "Microsoft.Communication/communicationServices" `
    --name $acsName --resource-group $azureResourceGroupName `
    --query id -o tsv

az eventgrid event-subscription create `
    --name "tpe-incoming-call" `
    --source-resource-id $acsResourceId `
    --endpoint-type webhook `
    --endpoint $eventGridEndpointUrl `
    --included-event-types "Microsoft.Communication.IncomingCall" `
    --advanced-filter data.to.rawId StringContains $teamsResourceAccountObjectId
```

The `advanced-filter` on `data.to.rawId` narrows delivery to **just this RA's calls**, so a single ACS resource can serve multiple resource accounts cleanly. Drop the filter only if you want every IncomingCall on the resource.

> **First delivery does a validation handshake.** Your webhook must respond to the Event Grid subscription validation `OPTIONS`/`POST` request the first time, or the subscription will go into `AwaitingManualAction` and never deliver events. ACS Call Automation samples handle this via `SubscriptionValidationEventData`.

## Step 9 — End-to-end verification

| # | Check | How |
|---|-------|-----|
| 1 | RA is bound correctly | `Get-CsOnlineApplicationInstance -Identity $teamsResourceAccountUpn` → `ApplicationId` = your Entra App Client ID, `AcsResourceId` = your `acsGlobalId`. |
| 2 | Phone number is still assigned | `Get-CsPhoneNumberAssignment -TelephoneNumber $teamsPhoneNumber` → `AssignedPstnTargetId` = RA Object ID. |
| 3 | Admin consent is granted | Azure portal → Entra ID → App registrations → your app → API permissions → status `Granted for <tenant>`. |
| 4 | TPE assignment exists | The verify GET in Step 7 returns `200`. |
| 5 | Bot exists with correct AppId/TenantId | `az bot show --name <bot> --resource-group <rg> --query "{appId:properties.msaAppId,tenantId:properties.msaAppTenantId}"`. |
| 6 | Event Grid subscription is `Enabled` | Azure portal → ACS resource → **Events** → `tpe-incoming-call` shows `Enabled` (not `AwaitingManualAction`). |
| 7 | End-to-end test | Place a PSTN call to `$teamsPhoneNumber`. Your callback should log an `IncomingCall` event whose `to.rawId` is `28:orgid:<RA Object ID>`. |

---

## Doing all of the above with the scripts

The same flow boils down to one command if your config file is filled in:

```powershell
# tpe-config.json: set teams.existingEntraAppClientId = $null (you don't have one yet),
# leave azure.acsName/acsGlobalId pointing at your existing ACS,
# and set azure.eventGrid.endpointUrl to your webhook.

.\eng\scripts\setup_tpe.ps1 -ConfigFile .\eng\scripts\tpe-config.json
```

Because every phase is idempotent ([setup_tpe_teams.ps1](../../../scripts/teams-extensibility/setup_tpe_teams.ps1) and [setup_tpe_azure.ps1](../../../scripts/teams-extensibility/setup_tpe_azure.ps1)):

- The existing Resource Account is detected (no `New-CsOnlineApplicationInstance`); only the bind + sync happen.
- The existing phone number assignment is detected and skipped.
- The existing license is detected and skipped.
- The existing ACS resource is auto-discovered and used as-is.
- Only Steps 1–4 + 6–8 above are actually executed; Step 5 reduces to the bind + sync because the RA is already there.

If the RA is currently bound to a Teams first-party `ApplicationId`, the script will warn and rebind it to your Entra app — the only change visible to end-users is that calls now route through ACS instead of into Teams' Auto Attendant/Call Queue handlers.
