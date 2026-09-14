# Contact Center AI Agent banking demo

An ASP.NET Core host for the accelerator's call runtime. It uses **Azure Voice
Live first**, **streaming DTMF fallback**, real ACS Call Automation and SMS
connectors, and an explicitly **in-memory demo bank**. No real bank or card system
is connected.

## Caller experience

1. Teams/TPE delivers an incoming call through ACS and Event Grid.
2. The host authenticates delivery, reserves call capacity, and answers with
   bidirectional 24 kHz PCM media and DTMF enabled.
3. Voice Live offers balance, debit-card activation, an operator, or finish.
   The equivalent keypad menu is `1`, `2`, `0`, or `9`.
4. Balance or card access requires an SMS OTP. ANI selects a configured customer;
   it is not authentication. The code is sent **only to that customer's phone on
   file**, and collected by the accelerator's trusted DTMF collector, not the model.
5. A current OTP proof allows balance lookup. Card activation additionally requires
   pressing `1` at the confirmation stage. A model-generated activation transition
   without that keypad event is denied.
6. Unknown callers, exhausted/unavailable verification, or verification silence
   route to the configured operator. Outside verification, the caller can choose
   the operator directly.
7. Transfer acceptance ends only the local bot session, not the operator call.
   Transfer failure plays an apology. Finish plays an ACS-controlled goodbye and
   hangs up only after its matching playback callback, with a timeout backstop.

The [workflow](Workflows/demo-bank.yaml) defines the process. The
[banking actions](Services/BankingActions.cs) use the same authorization/action
path for realtime and DTMF. Demo activation is idempotent for the customer's one
configured card; state resets when the process restarts.

## Run safely without Azure

`BankingDemo:Enabled` defaults to `false`. In this mode no calling/model/SMS
services are instantiated, and the call routes return 503.

```powershell
dotnet run --project .\code\ContactCenter.AIAgent\ContactCenter.AIAgent.csproj --launch-profile http
```

- Status: `http://localhost:5071/demo`
- Development OpenAPI: `http://localhost:5071/openapi/v1.json`
- [Request examples](ContactCenter.AIAgent.http)

Preview mode checks host startup and API shape; it is not a simulated phone call.
For automated no-Azure E2E coverage, run the
[host integration tests](../test/Agents.AI.ContactCenter.Tests/DemoHost/BankingDemoHostTests.cs).
They exercise HTTP authentication, a TestServer WebSocket, real workflow/auth/action
execution, and fake external connectors.

## Configure live calling

Use user secrets for local configuration or the deployment's secret/configuration
provider. Do not place real customer records, tokens, or connection strings in
committed settings. Then set `BankingDemo:Enabled=true`.
Environment variables use double underscores, for example
`BankingDemo__PublicBaseUri` and `BankingDemo__Customers__0__PhoneNumber`;
user-secret keys use colons. The project already has a user-secrets ID.
Restart after configuration changes. Live calls, SMS, Speech, and model usage
incur Azure/service charges.

| Setting under `BankingDemo` | Purpose |
| --- | --- |
| `PublicBaseUri` | Public HTTPS URL ending in `/`; callbacks and WSS routes are built beneath it |
| `AcsEndpoint` | ACS resource endpoint |
| `AcsAudience` | Expected ACS callback/media JWT audience for that resource; use the actual configured resource audience |
| `AcsConnectionString` | Optional secret override for ACS/SMS; otherwise Entra credentials are used |
| `SpeechEndpoint` | Custom-domain multi-service AI resource HTTPS endpoint used for trusted OTP/scripted prompts and ACS final announcements |
| `VoiceLiveEndpoint` | Voice Live resource HTTPS base endpoint, not a project URL or the full WSS/query URI |
| `VoiceLiveModel` | An available Voice Live model name; no deployment is created by this app |
| `VoiceName` | Voice supported by both configured speech paths |
| `SmsFrom` | Approved ACS SMS sender in E.164 format |
| `OperatorNumber` | Live operator/queue destination in E.164 format; must not point back to this demo's ingress |
| `RedirectCallerId` | ACS-authorized voice caller ID for pre-answer PSTN overflow redirect; not automatically the SMS sender |
| `EventGridTenantId` | Entra tenant issuing Event Grid delivery tokens |
| `EventGridAudience` | Audience of this webhook API's Entra resource |
| `EventGridObjectId` | Allowed Event Grid delivery identity's `oid`; not the application/client ID |
| `ManagedIdentityClientId` | Optional user-assigned managed identity client ID in hosted environments |
| `Customers` | Configured demo customer ID, display name, phone on file, balance, and debit-card last four |
| `OtpInputTimeoutSeconds` | Silence-to-operator timeout, default 60 seconds |
| `MediaConnectTimeoutSeconds` | Media/provider startup allowance, default 45 seconds |
| `MaximumCallSeconds` | Demo duration limit before operator escalation, default 600 seconds |

Customer record shape, using fictional values:

```json
{
  "Id": "demo-customer",
  "DisplayName": "Demo Customer",
  "PhoneNumber": "+15555550101",
  "CardLastFour": "1234",
  "Balance": 1250.50
}
```

Replace the fictional number privately with a test phone you control and are
authorized to call/message. There is no HTTP endpoint for retrieving OTPs, adding
customers, reading balances, or activating cards without going through the call.

Development uses `AzureCliCredential`; hosted environments use system-assigned
or configured user-assigned managed identity. Sign into Azure CLI as an authorized
identity before running live in Development. Setting `AZURE_CLIENT_SECRET` alone
does not select a service principal in this app's explicit credential policy.

Configure these identity boundaries separately:

- **Host to ACS/SMS:** follow the
  [ACS Entra authorization guide](https://learn.microsoft.com/azure/communication-services/quickstarts/identity/service-principal).
  Scope access to the selected ACS resource; do not copy the quickstart's
  subscription-wide Contributor example into production. A private ACS connection
  string is an optional demo alternative; it does not authenticate Speech/Voice Live.
- **Host to Voice Live/Speech:** the
  [Voice Live quickstart](https://learn.microsoft.com/azure/ai-services/speech-service/voice-live-quickstart)
  documents `Cognitive Services User` for keyless access. Review the
  [current authentication requirements](https://learn.microsoft.com/azure/ai-services/speech-service/voice-live-how-to#authentication)
  for the selected resource/features, including Foundry roles where applicable.
  Use a custom-domain resource endpoint for Entra-authenticated Speech.
- **ACS to Speech for final announcements:** connect ACS to the multi-service AI
  resource following the
  [ACS Cognitive Services integration guide](https://learn.microsoft.com/azure/communication-services/concepts/call-automation/azure-communication-services-azure-cognitive-services-integration).
  This grants the **ACS resource's** managed identity `Cognitive Services User`;
  granting only the app identity is insufficient.
- **Event Grid to this API:** follow
  [Entra-protected webhook delivery](https://learn.microsoft.com/azure/event-grid/secure-webhook-delivery)
  for the destination application, sender, and subscription-writer permissions.
  These are not the host's outbound Azure credentials.

Check region/model/voice availability and quota before enabling calls. SMS country,
sender, recipient, and carrier restrictions still apply. No roles or subscriptions
are created automatically by this application.

## Connect Event Grid and ACS

- Keep the existing Teams Phone/TPE onboarding and Azure resource configuration.
  This change provisions nothing.
- Configure the ACS system-topic subscription for
  `Microsoft.Communication.IncomingCall`, using **Event Grid event schema**, at
  `{PublicBaseUri}events/incoming-call`.
- Configure Entra-protected delivery with the audience and allowed sender object
  ID above. The host accepts valid tenant v1/v2 issuers, validates audience, and
  separately checks `oid`.
- A single `SubscriptionValidationEvent` can receive the harmless validation-code
  echo without authentication. It cannot answer a call; all real deliveries require
  the configured identity. Mixed/real event batches do not inherit this exception.
- The answer response supplies per-call callback and media URLs. ACS callbacks
  use CloudEvents; its media connection uses WSS.
- If `PublicBaseUri` contains a path prefix, the reverse proxy must strip it before
  forwarding to these root-relative API routes. Keep TLS/WSS enabled at ingress.
- ACS JWTs are validated using the official Call Automation OpenID metadata,
  issuer, signing keys, audience, and lifetime. Tokens are accepted from headers,
  never from URL query parameters. Callback connection IDs must match the route.

Official references:
[secure ACS webhooks/media](https://learn.microsoft.com/azure/communication-services/how-tos/call-automation/secure-webhook-endpoint),
[audio streaming](https://learn.microsoft.com/azure/communication-services/how-tos/call-automation/audio-streaming-quickstart),
and [SMS sending](https://learn.microsoft.com/azure/communication-services/quickstarts/sms/send).

## Capacity, lifecycle, and observability

`AgentTiers` configures realtime and DTMF capacity and whether mid-call degradation
is allowed. The checked-in limits are deliberately small demo limits, not Azure
quota recommendations. Set realtime capacity to zero to exercise deterministic
fallback. If both bot tiers are full, an admitted incoming call is redirected to
the configured operator without answering it in the bot.

Answer/redirect side effects use the accelerator's incoming-call admission record.
An ambiguous answer failure is not automatically retried. The host preserves its
route briefly to process late callbacks and stop an orphaned answered call.
ACS/SMS SDK retries are disabled for these demo side effects; network waits are
bounded. The host also watches missing media, final playback, transfer, and call
duration. Callback IDs are deduplicated within each active call.

Canonical call context is established around creation, media processing, callbacks,
and transfer. Existing service defaults export through configured OTLP. No OTP is
logged; authentication informational logs are suppressed in sample settings.

## Deployment and production boundaries

- **Run one instance.** Call routing, bank, admission, and challenges use local memory.
  Do not scale replicas without distributed routing/state/challenge implementations.
- Operator escalation is a blind PSTN transfer/redirect, not an integrated human
  agent desktop or a consultative transfer.
- The same media socket remains in use for realtime-to-streaming-DTMF degradation.
  A lost socket triggers operator escalation; this is not automatic migration of
  media to another replica.
- Registered customer ANI can be spoofed; only OTP proof authorizes account actions.
  Production banking needs stronger risk controls, consent, lockout, and account APIs.
- In-memory demo state and idempotence do not survive restart. They are not a
  production transactional guarantee.
- Actual Azure/TPE routing, SMS delivery, model behavior, playback, transfer,
  scaling, and failure recovery require live validation with your configuration.
- The Dockerfile uses `code` as its build context and includes referenced projects.
  Container build/deployment and native Linux Speech dependencies are not claimed
  as tested by local .NET tests.
- Existing user edits to Aspire composition and the solution were left intact.
