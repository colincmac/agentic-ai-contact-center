# Custom IVR Call Flow: PSTN → Teams RA → ACS Call Automation → Dynamics CCaaS

Here's the end-to-end call flow broken into the major hops, with the key control-plane messages on each leg.

---

## 1. PSTN → Teams Phone Resource Account

```
[Caller PSTN] ──► [MS PSTN connectivity*] ──► [Teams Phone System] ──► [Resource Account (RA)]
```

- The caller dials the PSTN DID assigned to the RA.
- Connectivity into Teams is one of: **Microsoft Calling Plans**, **Operator Connect**, or **Direct Routing (SBC)**.
- The number is hosted on a **Resource Account** — a disabled Entra ID user object licensed with Teams Phone Resource Account + a calling plan/OC/DR number.
- Normally an RA would route to an Auto Attendant or Call Queue. Here it is instead routed via **Teams Phone Extensibility (TPE)**.

## 2. Teams Phone Extensibility (TPE) → ACS

```
[Resource Account] ──TPE binding──► [ACS Communication Resource]
```

- TPE allows the RA to be associated with an **ACS resource** (the "Azure Communication Services – Teams Phone integration"). This is set up via `Set-CsOnlineApplicationInstanceAssociation` style configuration that points the RA at an ACS resource ID.
- When the call arrives at the RA, Teams hands it off to the bound ACS resource. From this point forward, **ACS owns the media path and signaling for the call**, but the call still consumes a Teams Phone number/license.
- ACS publishes an **`Microsoft.Communication.IncomingCall`** event to **Event Grid**. The event payload contains:
  - `from` (caller PSTN URI)
  - `to` (the Teams DID)
  - `callerDisplayName`
  - **`incomingCallContext`** — opaque token required to answer the call
  - `correlationId`, `serverCallId`



### What actually happens on the wire
Conceptually the flow is **Caller → Teams → ACS → (notify your app) → your app tells ACS to answer**. Teams does not transfer the call to your app; Teams transfers (internally) to **ACS**, and ACS then asks your app what to do.

```
Caller PSTN
   │  SIP/RTP (or PSTN TDM via carrier)
   ▼
Microsoft PSTN connectivity (Calling Plans / Operator Connect / Direct Routing SBC)
   │  SIP INVITE → Teams Phone System
   ▼
Teams Phone System
   │  Looks up the dialed DID → finds it assigned to a Resource Account (RA)
   │  RA has a TPE association to an ACS resource (the "Azure Communication Services
   │  Telephony" application instance, app ID 1ec4cb5b-…)
   │
   │  ── Internal Microsoft backend handoff (NOT a SIP REFER you can see) ──►
   ▼
ACS calling backend
   │  Materializes the call as an ACS call object, generates an `incomingCallContext`
   │  (an opaque, signed JWT-like token representing the call leg)
   │
   │  Publishes Microsoft.Communication.IncomingCall to Event Grid
   ▼
Your IVR app (AKS)
   │  Calls AnswerCall(incomingCallContext, callbackUri) via Call Automation SDK
   ▼
ACS answers the call → media path is now ACS ⇄ Caller; signaling/control is your app ⇄ ACS (HTTPS)
```
<details>
<summary><b>Why it is not a SIP transfer</b></summary>

A SIP transfer (REFER) implies one SIP UA telling another to re-INVITE a third party — visible signaling between independent endpoints. In TPE, none of those preconditions are true:

- **Same trust boundary.** Teams Phone System and ACS are both Microsoft 1st-party services. The handoff happens over Microsoft-internal APIs/fabric, not over a public SIP trunk.
- **No new INVITE is issued toward your app.** Your app never sees SIP. It sees an **HTTPS webhook (Event Grid CloudEvent)** describing an incoming call, and an **opaque `incomingCallContext`** it must present back to ACS to claim the call.
- **No media re-negotiation between Teams and ACS that you control.** SDP, codecs, transcoding (e.g., SILK ↔ G.711/Opus), and DTMF (RFC 2833 vs in-band) are handled by the Teams↔ACS interop layer.
- **The call is not "moved off" Teams.** The DID, licensing, and PSTN leg remain anchored on the Teams Phone side. ACS becomes the **call control + media endpoint** for the Teams-side participant. That is closer to a **delegation / federation** model than a transfer.

If you packet-capture an SBC in a Direct Routing setup, you'll see the original INVITE from your SBC into Teams — and that's it. The Teams→ACS hop is not exposed to the SBC.
</details>

## 3. Event Grid → Custom IVR App in AKS (Answer)

```
[Event Grid] ──webhook──► [IVR App in AKS] ──AnswerCall──► [ACS Call Automation]
                                            ◄──CallConnected── (to callback URI)
```

- Your AKS app exposes an HTTPS webhook subscribed to the `IncomingCall` event type on Event Grid (one-time validation handshake required).
- On receipt, the app decides to answer and invokes `CallAutomationClient.AnswerCallAsync(incomingCallContext, callbackUri)` (Call Automation SDK).
- The `callbackUri` is a second HTTPS endpoint on your AKS app — **all subsequent mid-call events** (`CallConnected`, `PlayCompleted`, `RecognizeCompleted`, `RecognizeFailed`, `CallDisconnected`, `CallTransferAccepted`, etc.) are POSTed there as **CloudEvents**, correlated by `callConnectionId`.
- After ACS connects media, you receive a `CallConnected` event — the IVR is now in control.

> Tip: keep the IncomingCall webhook and the mid-call callback on different routes; the first is Event Grid–shaped, the second is Call Automation CloudEvents–shaped.

---

## 4. IVR Loop: Play Audio + Recognize DTMF

```
[IVR App] ──PlayMedia──────────────────────► [ACS] ──audio──► caller
[IVR App] ──StartRecognizing(Dtmf)─────────► [ACS]
                                             ◄── RecognizeCompleted (digits) ── [IVR App]
[IVR App] ──(branch in DTMF tree)──► next PlayMedia / StartRecognizing ...
```

- **Play prompts**: `CallMedia.PlayToAllAsync(...)` or `PlayAsync(...)` with either:
  - `FileSource` (URL to a wav/mp3 reachable from ACS), or
  - `TextSource` (Cognitive Services TTS — requires the ACS resource to have a Cognitive Services connection).
- **Collect DTMF**: `CallMedia.StartRecognizingAsync(CallMediaRecognizeDtmfOptions)` with parameters:
  - `interToneTimeout`, `initialSilenceTimeout`
  - `maxTonesToCollect` (e.g., 1 for a menu, more for account number entry)
  - `stopTones` (e.g., `#`)
  - `interruptPrompt` (barge-in)
  - `operationContext` — the **menu node ID**; echoed back in events so you know which menu is responding.
- ACS posts back `RecognizeCompleted` (with `Tones`) or `RecognizeFailed` (timeout / no input). Your app consults its menu state machine (often keyed by `callConnectionId` + `operationContext`) and issues the next Play/Recognize. This is your DTMF tree traversal.

State for the IVR tree typically lives in a distributed cache (Redis) keyed by `callConnectionId`, since AKS pods are stateless and Event Grid / Call Automation callbacks may land on any pod.

---

## 5a. Resolution Path — End the Call

```
[IVR App] ──HangUp(forEveryone:true)──► [ACS] ──BYE──► [Teams] ──► [Caller]
                                          ◄── CallDisconnected ── [IVR App]
```

- `CallConnection.HangUpAsync(forEveryone: true)` ends the call for all parties.
- ACS emits a final `CallDisconnected` so the app can clear cache/state and emit telemetry/CDRs.

---

## 5b. Escalation Path — Transfer to Dynamics CCaaS

Dynamics 365 **Contact Center / Customer Service voice** is itself built on ACS, so the transfer target is reachable as an ACS/Teams identity or, more commonly, as a PSTN number exposed by the CCaaS workstream.

```
[IVR App] ──TransferCallToParticipant(target)──► [ACS Call Automation]
                                                  ◄── CallTransferAccepted ── [IVR App]
                                                  ◄── CallTransferFailed   ── (on failure)
```

- API: `CallConnection.TransferCallToParticipantAsync(TransferToParticipantOptions)`
- The `target` `CommunicationIdentifier` is one of:
  - `PhoneNumberIdentifier` (E.164) — a DID owned by the Dynamics CCaaS workstream / queue
  - `MicrosoftTeamsUserIdentifier` — if escalating to a Teams agent
  - `MicrosoftTeamsAppIdentifier` / `CommunicationUserIdentifier` for ACS-native or Teams-interop endpoints exposed by the CCaaS tenant
- After ACS issues `CallTransferAccepted`, your app's call connection ends (you are no longer in the media path); the CCaaS workstream takes over and runs its own Call Automation / workflow against the same call.
- Middleware (architecture TBD) manages hydrating the CCaaS Dataverse instance. The CCaaS workstream hydrates call context based on the call context identifier passed through the custom call context headers.

> The on-the-wire transport (VoIP vs SIP) and which `CustomCallingContext` bucket carries your IVR context depend on where the destination DID lives — same-tenant transfers ride the Microsoft calling backbone as **VoIP** (use `VoipHeaders`); cross-tenant or SBC-routed transfers are real **SIP** transfers (use `SipHeaders` + UUI). Same-tenant is the common case for in-tenant Dynamics CCaaS. Detailed rules, header limits, and the C# call site are in [`transfer-patterns.md`](transfer-patterns.md). When the IVR needs to *stay on the call* (VIP / supervisor takeover) the same transport rule applies to `AddParticipant` — see the consultative-transfer section in the same document.

---

## End-to-End Sequence (Happy-Path Escalation)

```
Caller PSTN
   │  (1) dial DID
   ▼
MS PSTN Connectivity ──► Teams Phone System ──► Resource Account
                                                      │ (2) TPE routing
                                                      ▼
                                                ACS Resource
                                                      │ (3) IncomingCall → EventGrid
                                                      ▼
                                              IVR App (AKS) ── AnswerCall ──► ACS
                                                      ◄── CallConnected ──
                                                      │
                                                      │ (4) PlayMedia / StartRecognizing(DTMF)  ◄── loop
                                                      │           ◄── RecognizeCompleted ──
                                                      ▼
                                              Branch decision
                                                ├─ (5a) HangUp ──► CallDisconnected
                                                └─ (5b) TransferCallToParticipant(Dynamics queue DID/SIP)
                                                              ──► CallTransferAccepted
                                                              ──► [Dynamics CCaaS owns the call]
```


## Things worth being deliberate about

| Area | Why it matters |
|---|---|
| **Webhook idempotency** | Event Grid and Call Automation both retry. Dedup on `id` / `(callConnectionId, sequenceNumber)`. |
| **State store** | Use Redis (or similar) keyed by `callConnectionId` for menu position; AKS pods are interchangeable. |
| **`operationContext`** | Set on every Play/Recognize so the callback tells you *which* menu node fired the event. |
| **Callback URI auth** | Use a per-call signing secret in the path or HMAC validation; the URI is internet-reachable. |
| **Cognitive Services link** | Required if you use `TextSource` (TTS) or want to swap to speech recognition later. |
| **Custom calling context on transfer** | The cleanest way to pass IVR context (intent, collected digits, language) into Dynamics CCaaS so the agent gets a screen-pop with state. Use `VoipHeaders` for same-tenant transfers (Microsoft backbone, no SIP signaling) and `SipHeaders` + UUI only for cross-tenant / SBC paths. The receiving side must read from the matching bucket on its `IncomingCall` event. |
| **Failure modes** | Handle `RecognizeFailed` (no input → re-prompt, max retries → operator), `CallTransferFailed` (queue closed → fallback Play + HangUp or voicemail), and `CallDisconnected` mid-flow (caller hangup → cleanup). |
| **Telemetry** | Correlate your app logs with ACS using `correlationId` / `serverCallId` from the IncomingCall payload — invaluable when diagnosing media or transfer issues with support. |
| **Licensing/path** | The DID is consumed on the **Teams** side; the **media + automation** runs in ACS. Both bills apply. |

---

## The mechanism, more precisely

1. **Binding.** The Resource Account is associated to ACS using:
   - `New-CsOnlineApplicationInstance` to create the RA,
   - PSTN number assignment to the RA, and
   - `Set-CsOnlineApplicationInstanceAssociation` (or the ACS portal "Telephony → Direct routing/Teams numbers") wiring the RA to the **ACS Telephony application instance** (the well-known ACS 1st-party app object in the tenant).

2. **Routing decision in Teams.** When a call hits the DID, Teams Phone System sees that the RA is bound to the ACS app instance and routes the call leg into the ACS calling backend instead of an Auto Attendant / Call Queue.

3. **ACS materializes the call.** ACS creates a call resource, mints an `incomingCallContext` (an opaque token that authorizes whoever holds it to answer/redirect/reject that specific call within a short TTL), and emits **`Microsoft.Communication.IncomingCall`** to Event Grid. Payload includes `from`, `to`, `callerDisplayName`, `incomingCallContext`, `correlationId`, `serverCallId`.

4. **Your app claims the call.** Your AKS app, subscribed to that Event Grid topic, decides what to do:
   - `AnswerCall(incomingCallContext, callbackUri)` — accept and start the IVR,
   - `RedirectCall(incomingCallContext, target)` — push it elsewhere without answering,
   - `RejectCall(incomingCallContext, reason)` — refuse it.
   These are **HTTPS calls to the ACS Call Automation API**, not SIP.

5. **Media.** Once answered, RTP/SRTP flows between the caller (via Microsoft's PSTN edge) and ACS's media stack. Your app is **not in the media path**; it is only in the control path (HTTPS webhooks + REST/SDK calls). That's why Play/Recognize work as REST verbs against ACS — ACS is the one actually playing audio and collecting DTMF.

---

## Mental model

Think of TPE as making the ACS resource look, to Teams Phone System, like just another **internal calling endpoint** that owns that DID — similar to how an Auto Attendant or Call Queue is an internal endpoint. Teams routes the call into that endpoint over the Microsoft backbone. ACS then exposes the call to *your* code via Event Grid + Call Automation, where the only "signaling" you ever touch is **HTTPS + JSON**.

---

## Practical implications for this IVR design

- **No SBC / SIP stack required in your app.** You only need: an HTTPS webhook for `IncomingCall`, an HTTPS webhook for mid-call CloudEvents, and the Call Automation SDK.
- **`incomingCallContext` is short-lived and single-use-ish.** Answer promptly; don't queue it for minutes.
- **Correlate with Teams via `correlationId` / `serverCallId`.** When opening a support case that spans Teams Phone and ACS, these are the IDs that let both sides find the same call.
- **Caller ID semantics.** `from` will reflect the PSTN caller; `to` is the Teams-assigned DID. If you later transfer to Dynamics CCaaS, you can preserve/override calling context via `CustomCallingContext` on `TransferCallToParticipant` — populate `VoipHeaders` for same-tenant targets (the common Dynamics CCaaS case) and `SipHeaders` + UUI only when the transfer actually traverses SIP (cross-tenant or off-net via SBC).
- **DTMF reliability.** Because Teams↔ACS interop normalizes DTMF, you generally get clean RFC 2833 events surfaced as `RecognizeCompleted` regardless of how the carrier delivered them. You don't need to worry about in-band tones.
- **Failover.** If your app doesn't answer/redirect/reject within ACS's window, the call fails on the ACS side; Teams does not "fall back" to an Auto Attendant unless you explicitly `RedirectCall` to one.

## See also

Detailed wire-level diagrams, validation handshake, and timing tables — previously inline appendices to this document — now live in dedicated files:

- [`sequence-diagrams.md`](sequence-diagrams.md) — full PSTN → Teams → ACS → IVR → CCaaS sequence diagram (former Appendix A).
- [`transfer-patterns.md`](transfer-patterns.md) — blind vs consultative transfer patterns and VoIP-vs-SIP transport rules (former §5b detail + Appendix C).
- [`../runbooks/event-grid-incomingcall-subscription.md`](../runbooks/event-grid-incomingcall-subscription.md) — Event Grid subscription validation handshake (former Appendix B).
- [`../runbooks/timing-and-retries.md`](../runbooks/timing-and-retries.md) — concrete timeouts, retry schedules, suggested defaults, and the production hardening checklist (former Appendix D).

