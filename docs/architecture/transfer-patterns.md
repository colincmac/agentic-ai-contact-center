# Transfer Patterns: Blind vs Consultative, VoIP vs SIP

This file is the implementation reference for **escalating a call out of the custom IVR**. ADR-0005 fixes the *policy* (blind by default, consultative reserved for VIP/supervisor); this document covers the *mechanics* — when each transport applies, which custom-context bucket to use, and what the wire actually does.

The narrative for the rest of the call flow lives in [`call-flow.md`](call-flow.md). The detailed end-to-end sequence diagram is in [`sequence-diagrams.md`](sequence-diagrams.md).

## Blind transfer — `TransferCallToParticipant`

When the IVR has gathered enough context (selected workstream, collected ID, language) it hands the caller to Dynamics CCaaS in one shot. The call leaves the IVR completely and CCaaS becomes the new owner.

The IVR calls `TransferCallToParticipant(targetPhoneNumber, options)`. ACS issues the transfer to the target on the IVR's behalf and the IVR drops out of media and control. The IVR receives `CallTransferAccepted` (success) or `CallTransferFailed` (queue closed / no answer / rejected). Failed transfers should fall back to "all agents unavailable" + hangup, or — if the routing layer supports it — re-queue elsewhere.

### Transport choice: VoIP transfer vs SIP transfer

Whether ACS performs a **VoIP transfer** or a **SIP transfer** is determined by the *target endpoint*, not by an SDK switch. This drives which custom-context bucket your code must populate.

| Target of the transfer | Transport ACS uses | Where context goes |
|---|---|---|
| DID belonging to a Resource Account / workstream **in the same Azure Communication Services tenant** as the IVR (e.g. Dynamics CCaaS workstream provisioned alongside the IVR) | **VoIP transfer** over the Microsoft calling backbone — no SIP signaling visible to your app or to an SBC | `CustomCallingContext.VoipHeaders` (free-form, up to 1,000 entries, value ≤ 1,024 chars, no required prefix) |
| DID in a **different tenant** (cross-tenant Teams) | **SIP transfer** | `CustomCallingContext.SipHeaders` (≤ 5 custom headers, names must be `X-*` or `X-MS-Custom-*`, value ≤ 256 chars) and / or `CustomCallingContext.AddSipUui(...)` (UUI ≤ 256 chars) |
| Off-net PSTN target reached via your **Direct Routing SBC** | **SIP transfer** out the SBC | Same as cross-tenant — SIP headers + UUI |

Same-tenant is the common path for an in-tenant CCaaS deployment, so VoIP headers are the default in this architecture. If your topology spans tenants, plan for the much tighter SIP header limits (5 headers, ~256 chars per value) and decide what subset of the IVR context is critical enough to make the cut.

### C# — populating context for the same-tenant case

```csharp
var transferOptions = new TransferToParticipantOptions(
    new PhoneNumberIdentifier(workstreamPhoneNumber))
{
    OperationContext = "escalate-to-ccaas"
};

// Same-tenant target → VoIP transfer → use VoipHeaders.
transferOptions.CustomCallingContext.AddVoip("intent", session.SelectedIntent);
transferOptions.CustomCallingContext.AddVoip("digits", session.CollectedDigits);
transferOptions.CustomCallingContext.AddVoip("lang", session.LanguageCode);
transferOptions.CustomCallingContext.AddVoip("correlationId", session.CorrelationId);

// Cross-tenant or SBC target → switch to SIP headers + UUI:
// transferOptions.CustomCallingContext.AddSipX("X-MS-Custom-Intent", session.SelectedIntent);
// transferOptions.CustomCallingContext.AddSipUui(session.CorrelationId);

await callConnection.TransferCallToParticipantAsync(transferOptions);
```

This is the only place in the architecture where the same-tenant assumption shows up in code. If you ever introduce a cross-tenant or SBC-routed workstream, this is the call site that needs a branch.

## Consultative (attended) transfer — `AddParticipant` + `RemoveParticipant`

Different from the blind transfer above — here the IVR (or supervisor) **stays on the call**, brings the agent in, talks to them, and only then drops out. ADR-0005 gates this to VIP / supervisor takeover scenarios because of the extra cost and complexity.

```mermaid
sequenceDiagram
    autonumber
    actor Caller as Caller (PSTN)
    participant ACS as ACS Call Automation
    participant IVR as IVR App (AKS)
    participant Cache as State Store (Redis)
    participant Agent as Dynamics CCaaS Agent<br/>(ACS endpoint / DID)

    Note over Caller,IVR: Pre-condition: 1:1 call (Caller ⇄ IVR via ACS)<br/>callConnectionId = C1, established earlier

    %% ───────────── Hold caller ─────────────
    rect rgb(235, 245, 255)
    Note over IVR,Caller: 1. Park the caller on hold music
    IVR->>ACS: PlayToAll(holdMusic, loop = true,<br/>operationContext = "hold")
    ACS->>Caller: Hold audio (SRTP)
    end

    %% ───────────── Add agent as participant ─────────────
    rect rgb(255, 250, 230)
    Note over IVR,Agent: 2. Dial agent into the same call
    IVR->>ACS: AddParticipant(<br>  callConnectionId = C1,<br>  participant = Agent (PhoneNumber/CommunicationUser),<br>  sourceCallerId = serviceDID,<br>  invitationTimeoutInSeconds = 30,<br>  customCallingContext.VoipHeaders = {intent, digits, lang}<br>  (use SipHeaders + UUI only if the agent endpoint is cross-tenant / behind an SBC),<br>  operationContext = "agent-invite")
    ACS-->>IVR: 202 Accepted (invitationId)
    ACS->>Agent: Outbound INVITE / ACS push
    Agent->>ACS: Answer

    alt Agent accepts within timeout
        ACS->>IVR: AddParticipantSucceeded<br/>{participant = Agent, operationContext = "agent-invite"}
    else No answer / declined / timeout
        ACS->>IVR: AddParticipantFailed {reason}
        Note over IVR: Fallback: try next agent,<br/>or unhold + announce + blind transfer to queue
    end
    end

    %% ───────────── Consultation phase (caller still on hold) ─────────────
    rect rgb(240, 255, 240)
    Note over IVR,Agent: 3. Consultation — caller is on hold, IVR + Agent in audio
    Note over IVR: Optional: Mute caller, or use<br/>HoldParticipant(caller) so agent + IVR<br/>can speak privately
    IVR->>ACS: HoldParticipant(target = Caller)
    ACS->>Caller: Continue hold media
    Note over Agent: IVR (or supervisor bot) briefs agent:<br/>passes intent, digits, CRM context
    end

    %% ───────────── Bring caller back ─────────────
    rect rgb(250, 240, 255)
    Note over IVR,Caller: 4. Resume — three-way bridge
    IVR->>ACS: UnholdParticipant(target = Caller)
    ACS->>Caller: Audio resumed
    Note over Caller,Agent: Caller, IVR, Agent are now all in the call
    end

    %% ───────────── IVR drops out ─────────────
    rect rgb(255, 235, 245)
    Note over IVR,ACS: 5. IVR removes itself, leaving Caller ⇄ Agent
    IVR->>ACS: RemoveParticipant(<br/>  callConnectionId = C1,<br/>  participant = self (IVR identity),<br/>  operationContext = "ivr-drop")
    ACS->>IVR: RemoveParticipantSucceeded
    Note over Caller,Agent: Call continues 1:1 between Caller and Agent<br/>via ACS media IVR has no further events
    IVR->>Cache: Mark session "handed off (consultative)"
    end

    %% ───────────── Eventual hangup ─────────────
    rect rgb(235, 255, 250)
    Note over Caller,Agent: 6. Either party hangs up later
    alt Caller hangs up
        Caller->>ACS: BYE
    else Agent hangs up
        Agent->>ACS: BYE
    end
    ACS->>IVR: CallDisconnected (final, for telemetry/CDR)
    IVR->>Cache: Purge session, emit CDR
    end

    %% ───────────── Failure: agent declines mid-consult ─────────────
    rect rgb(255, 235, 235)
    Note over IVR,Agent: Failure — agent abandons during consultation
    alt Agent disconnects before IVR drops
        Agent->>ACS: BYE
        ACS->>IVR: ParticipantsUpdated {removed = Agent}
        IVR->>ACS: UnholdParticipant(Caller)
        IVR->>ACS: PlayToAll("Sorry, please hold while we find another agent")
        Note over IVR: Loop back to step 2 with next agent,<br/>or fall through to blind transfer / hangup
    end
    end
```

The same VoIP-vs-SIP transport rule applies on `AddParticipant` — same-tenant agent endpoint = VoIP transfer + `VoipHeaders`; cross-tenant or SBC target = SIP transfer + `SipHeaders` / UUI.

### Why use this over a blind transfer

| Aspect | Blind (`TransferCallToParticipant`) | Consultative (`AddParticipant` + `RemoveParticipant`) |
|---|---|---|
| IVR stays in call during handoff | No — drops immediately on `CallTransferAccepted` | Yes — until `RemoveParticipant` |
| Can brief the agent | No (rely on `CustomCallingContext` / screen-pop only — VoIP or SIP headers depending on tenancy) | Yes (live audio briefing, plus the same `CustomCallingContext` payload on `AddParticipant`) |
| Caller experience on agent reject | Lost unless you handle `CallTransferFailed` | Caller never disconnected; just stays on hold |
| Cost / complexity | Lower — fewer events, simpler state | Higher — must manage 3-party media + per-leg events |
| Best for | High-volume, well-known queues | VIP, complex escalations, supervisor takeover |

## See also

- [ADR-0005](../adr/0005-escalation-blind-vs-consultative-transfer.md) — the policy decision (blind by default).
- [`call-flow.md`](call-flow.md) — where in the overall flow the transfer fits.
- [`sequence-diagrams.md`](sequence-diagrams.md) — wire-level picture of the blind-transfer happy path.
- Microsoft Learn: [Pass contextual data between calls](https://learn.microsoft.com/en-us/azure/communication-services/how-tos/call-automation/custom-context) — authoritative limits on VoIP/SIP custom headers and UUI.
