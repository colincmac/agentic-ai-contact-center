# Detailed Sequence Diagram: PSTN → Teams → ACS → Custom IVR → Dynamics CCaaS

The wire-level happy-path escalation, end to end. The narrative for each band lives in [`call-flow.md`](call-flow.md); this file is the picture.

- **Blue band (1)** — public PSTN ingress and Teams routing decision.
- **Green band (2)** — the TPE handoff between Teams and ACS. Note this is an internal Microsoft delegation, not a SIP REFER, so there is no SIP signaling visible to your SBC/app on this leg.
- **Yellow band (3)** — `IncomingCall` arrives via Event Grid; your app answers via Call Automation. From here on, your app's signaling is HTTPS + CloudEvents, never SIP.
- **Purple band (4)** — the DTMF tree loop. Each menu node is a Play → Recognize cycle; `operationContext` carries the node ID through ACS so callbacks tell you exactly which menu fired the event. State lives in Redis keyed by `callConnectionId`, so any AKS pod can handle the next callback.
- **Red band (5a)** — clean self-service termination via `HangUp`.
- **Teal band (5b)** — blind transfer to Dynamics CCaaS via `TransferCallToParticipant`. When the workstream's DID is in the **same tenant** as the IVR's RA (the common case), this is a **VoIP transfer** on the Microsoft calling backbone — no SIP signaling leaves the fabric and IVR-collected context rides as **VoIP headers** in `CustomCallingContext.VoipHeaders` (1,000 headers, value ≤ 1,024 chars). Only when the target lives in a **different tenant** or behind your SBC (Direct Routing) is it a true **SIP transfer**, in which case context must go via `SipHeaders` + the SIP **UUI** header (≤ 5 headers with `X-*` / `X-MS-Custom-*` prefix, value ≤ 256 chars). The IVR drops out once `CallTransferAccepted` fires regardless of transport; the failure branch is shown explicitly so you don't lose the caller if the workstream rejects. Full transport-decision rules live in [`transfer-patterns.md`](transfer-patterns.md).

```mermaid
sequenceDiagram
    autonumber
    actor Caller as Caller (PSTN)
    participant PSTN as MS PSTN Connectivity<br>(Calling Plan / OC / DR SBC)
    participant Teams as Teams Phone System<br>(Resource Account + TPE binding)
    participant ACS as ACS Calling Backend<br>(Call Automation)
    participant EG as Azure Event Grid
    participant IVR as IVR App (AKS)<br>Call Automation SDK
    participant Cache as State Store<br>(Redis)
    participant CogSvc as Cognitive Services<br>(TTS, optional)
    participant Middleware as Middleware<br>(Dataverse - TBD)
    participant CCaaS as Dynamics 365<br>Contact Center (ACS-based)

    %% ───────────── 1. Inbound PSTN call ─────────────
    rect rgb(235, 245, 255)
    Note over Caller,Teams: 1. PSTN ingress
    Caller->>PSTN: Dial DID (TDM / SIP)
    PSTN->>Teams: SIP INVITE (to = DID)
    Teams->>Teams: Lookup DID → Resource Account<br>RA bound (TPE) to ACS app instance
    end

    %% ───────────── 2. Teams → ACS handoff (TPE) ─────────────
    rect rgb(240, 255, 240)
    Note over Teams,ACS: 2. Internal MS handoff (NOT a SIP REFER)
    Teams-->>ACS: Internal call delegation<br>(Microsoft backbone, 1st-party)
    ACS->>ACS: Materialize call object<br>Mint incomingCallContext (opaque, short-lived)<br>Generate correlationId / serverCallId
    end

    %% ───────────── 3. IncomingCall event → IVR answers ─────────────
    rect rgb(255, 250, 230)
    Note over ACS,IVR: 3. Notify app + answer
    ACS->>EG: Publish Microsoft.Communication.IncomingCall<br>{from, to, incomingCallContext, correlationId, serverCallId}
    EG->>IVR: POST /events/incoming-call (CloudEvent)
    IVR->>Cache: Reserve session by correlationId
    IVR->>ACS: AnswerCall(incomingCallContext, callbackUri)<br>[HTTPS / Call Automation REST]
    ACS-->>IVR: 202 Accepted (callConnectionId)
    ACS->>Caller: Establish media (SRTP)
    ACS->>IVR: POST callbackUri — CallConnected<br>{callConnectionId, correlationId}
    IVR->>Cache: Store {callConnectionId → menuNode = "root"}
    end

    %% ───────────── 4. IVR loop: Play + Recognize DTMF ─────────────
    rect rgb(250, 240, 255)
    Note over IVR,ACS: 4. DTMF tree traversal (loops per menu node)

    loop For each menu node until terminal
        IVR->>Cache: Read current menuNode
        alt Prompt is TTS
            IVR->>ACS: PlayToAll(TextSource, operationContext = nodeId)
            ACS->>CogSvc: Synthesize speech
            CogSvc-->>ACS: PCM audio
        else Prompt is pre-recorded
            IVR->>ACS: PlayToAll(FileSource(url), operationContext = nodeId)
        end
        ACS->>Caller: Stream audio (SRTP)
        ACS->>IVR: PlayCompleted {operationContext = nodeId}

        IVR->>ACS: StartRecognizing(Dtmf,<br>maxTones, stopTones=#, interToneTimeout,<br>interruptPrompt=true, operationContext = nodeId)
        Caller->>ACS: DTMF tones (RFC 2833)

        alt Caller entered digits in time
            ACS->>IVR: RecognizeCompleted<br>{tones:[...], operationContext = nodeId}
            IVR->>Cache: Update menuNode = next(nodeId, tones)
        else Timeout / no input / invalid
            ACS->>IVR: RecognizeFailed {reason, operationContext = nodeId}
            IVR->>Cache: Increment retryCount(nodeId)
            alt retryCount < max
                Note over IVR: Re-prompt same node
            else Max retries exceeded
                Note over IVR: Fall through to escalate or hangup
            end
        end
    end
    end

    %% ───────────── 5a. Resolution: Hangup ─────────────
    rect rgb(255, 235, 235)
    Note over IVR,Caller: 5a. Resolved in IVR — end the call
    alt Intent resolved by self-service
        IVR->>ACS: PlayToAll("Thanks, goodbye")
        ACS->>Caller: Audio
        ACS->>IVR: PlayCompleted
        IVR->>ACS: HangUp(forEveryone = true)
        ACS->>Caller: SIP BYE (via Teams/PSTN edge)
        ACS->>IVR: CallDisconnected
        IVR->>Cache: Purge session, emit CDR/telemetry
    end
    end

    %% ───────────── 5b. Resolution: Transfer to Dynamics CCaaS ─────────────
    rect rgb(235, 255, 250)
    Note over IVR,CCaaS: 5b. Escalate — blind transfer to Dynamics CCaaS
    alt Escalation path
        IVR->>ACS: PlayToAll("Connecting you to an agent")
        ACS->>Caller: Audio
        ACS->>IVR: PlayCompleted

        IVR->>Middleware: Store call state by contextId<br>{intent, collectedDigits, lang, correlationId}

        IVR->>ACS: TransferCallToParticipant(<br>target = PhoneNumber of CCaaS workstream,<br>customCallingContext.VoipHeaders = {intent, collectedDigits, lang, correlationId}<br>(use SipHeaders + UUI only for cross-tenant / SBC targets))

        ACS->>CCaaS: VoIP transfer over MS calling backbone (same tenant)<br>VoIP headers ride along — no SIP signaling on this hop<br>(cross-tenant target → SIP transfer with SIP headers + UUI instead)
        CCaaS-->>ACS: Accept

        alt Transfer accepted
            ACS->>IVR: CallTransferAccepted
            Note over IVR,ACS: IVR drops out of media + control <br>CCaaS now owns the call
            ACS->>IVR: CallDisconnected (IVR leg)
            IVR->>Cache: Mark session escalated, emit telemetry

            CCaaS->>Middleware: Fetch call context from contextId
            Middleware-->>CCaaS: Hydrate call context

            CCaaS->>CCaaS: Workstream routing → queue → agent<br>(screen-pop using customCallingContext)
            CCaaS->>Caller: Agent media (via ACS)
        else Transfer failed (queue closed / no answer / rejected)
            ACS->>IVR: CallTransferFailed {reason}
            IVR->>ACS: PlayToAll("All agents unavailable, please try later")
            ACS->>Caller: Audio
            IVR->>ACS: HangUp(forEveryone = true)
            ACS->>IVR: CallDisconnected
            IVR->>Cache: Purge, emit failure telemetry
        end
    end
    end
```

## See also

- [`call-flow.md`](call-flow.md) — narrative walkthrough of each band.
- [`transfer-patterns.md`](transfer-patterns.md) — VoIP-vs-SIP transport rules and the consultative-transfer alternative.
- [`../runbooks/event-grid-incomingcall-subscription.md`](../runbooks/event-grid-incomingcall-subscription.md) — the validation handshake that has to succeed before band (3) can ever fire.
- [`../runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md) — concrete timeouts on every arrow in this diagram.
