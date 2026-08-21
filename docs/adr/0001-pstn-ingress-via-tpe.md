# ADR-0001 — PSTN ingress via Teams Phone Extensibility (Resource Account → ACS)

- **Status:** Accepted
- **Date:** initial deployment

## Context

The platform needs an inbound PSTN entry point for callers to reach the AI voice agent. The reference architecture standardizes on **Teams Phone Extensibility (TPE)**: a DID is assigned to a Teams **Resource Account** (RA), the RA is bound to the Azure Communication Services (ACS) resource via the ACS Telephony 1st-party application instance, and Teams delegates the call to the ACS calling backend over the Microsoft backbone. This is detailed in [`call-flow.md` §1–2](../architecture/call-flow.md), [`teams-extensibility.md`](../runbooks/teams-configuration/teams-extensibility.md), [`tpe-onboarding-guide.md`](../runbooks/teams-configuration/tpe-onboarding-guide.md), and [`tpe-brownfield.md`](../runbooks/teams-configuration/tpe-brownfield.md).

Native ACS DIDs and customer-managed SBCs bridging directly into ACS are **not supported deployment modes** for this platform. The target customers already operate Teams Phone at scale and want their existing DIDs, number-management workflows, compliance recording integrations, and Teams-side reporting to remain authoritative. They also escalate calls to Dynamics 365 Contact Center (itself ACS-based), making an ACS-native call object on the inside the right shape for downstream handoffs.

What remains a real choice is **how the DID gets onto the Teams Resource Account in the first place**. Teams Phone supports three PSTN connectivity options for the RA's number, and the choice is per-customer (and often per-region):

1. **Microsoft Calling Plan** — Microsoft is the carrier. Numbers are acquired or ported into the Microsoft tenant and a Calling Plan license is assigned to the Resource Account. Fastest to stand up; limited to the countries/regions where Microsoft sells Calling Plans, and the customer accepts Microsoft as carrier of record.
2. **Operator Connect (OC)** — a Microsoft-certified operator provides the PSTN trunk and the numbers, provisioned through the Teams admin center. No customer-managed SBC. Available where the customer's preferred operator has an OC offering; keeps the existing carrier relationship and (often) existing contracts intact.
3. **Direct Routing (DR) into Teams** — the customer (or a partner) operates a certified SBC that terminates PSTN and connects into Teams Phone. Maximum control over carrier, codec, and PSTN-leg compliance posture; the customer owns SBC operations, certificates, and SIP trunk management. Note this is Direct Routing **into Teams**, not into ACS — once the call is on the RA, the Teams → ACS hop is identical to the other two options.

In all three cases the Teams → ACS delegation, the ACS-side call object, and the agent app's Call Automation surface are identical. The choice only affects how the DID is carrier-terminated *before* it lands on the Resource Account.

## Decision

- **TPE is the only supported PSTN ingress** for this platform: the DID lives on a Teams Resource Account, the RA is bound to the ACS resource, and Teams delegates to ACS. Native ACS DIDs and SBC-direct-to-ACS are out of scope.
- The **PSTN connectivity that backs the Resource Account's number is a per-customer decision** between **Calling Plan**, **Operator Connect**, or **Direct Routing into Teams**. The platform supports all three with the same downstream wiring.
- Default recommendation, in order: **Operator Connect** where the customer's carrier supports it (preserves carrier of record, no SBC ops); **Calling Plan** for greenfield or for regions where OC is unavailable but Calling Plans are; **Direct Routing into Teams** when the customer mandates a specific carrier, requires SBC-mediated compliance recording on the PSTN leg, or operates in a region without OC/Calling Plan coverage.

## Consequences

- The DID, licensing (Teams Phone Resource Account license + Calling Plan / OC / DR number), and PSTN edge stay anchored on the **Teams** side regardless of which connectivity option backs the RA. The customer's existing number-porting and compliance posture is preserved.
- Once the call reaches ACS, **ACS owns the media path and the call object**; the agent app interacts with the call through Call Automation only (HTTPS + CloudEvents — see [ADR-0002](0002-acs-call-automation-as-control-plane.md)). This is true for all three PSTN options.
- The handoff Teams → ACS is an **internal Microsoft delegation**, not a SIP REFER. The app never sees SIP and cannot packet-capture the Teams↔ACS leg. This drives the "no SIP stack" decision in ADR-0002 — and it means the platform code does not need to know which of Calling Plan / OC / DR is in use.
- DTMF arrives at ACS already normalized (RFC 2833) regardless of how the carrier delivered it. App-side DTMF handling does not need to worry about in-band tones or carrier differences — see [`call-flow.md` "DTMF reliability"](../architecture/call-flow.md).
- Provisioning is multi-step (Resource Account, Entra app, license assignment, RA→ACS binding, Agent Provisioning Service sync, Bot Service registration, Event Grid subscription) and is split across the **Teams tenant** and the **Azure tenant**. This is captured in [`tpe-onboarding-guide.md`](../runbooks/teams-configuration/tpe-onboarding-guide.md) (greenfield) and [`tpe-brownfield.md`](../runbooks/teams-configuration/tpe-brownfield.md) (existing RA / ACS). The PSTN-connectivity choice adds a step *before* this flow (acquire/port the number under Calling Plan, OC, or DR) but does not change the RA→ACS binding.
- Both the Teams Phone side and the ACS side bill for the call. The cost model must include Teams Phone licensing, the per-minute/per-number cost of the chosen PSTN option (Microsoft for Calling Plan, the operator for OC, the customer's SBC + carrier for DR), **and** ACS automation/media minutes.
- If the agent app does not answer/redirect/reject the `IncomingCall` within the ACS window (~60 s), the call **fails on the ACS side** — Teams does not silently fall back to an Auto Attendant. This shapes the answer-window SLA in [ADR-0003](0003-incomingcall-delivery-via-event-grid.md).
- Regional availability of Calling Plan and Operator Connect varies; customers in regions covered by neither must use Direct Routing into Teams. This is a deployment-planning concern, not a code-path concern.

## Alternatives considered

PSTN connectivity options *for the Teams Resource Account* (all three are supported; the choice is per-customer):

- **Microsoft Calling Plan.** Recommended for greenfield deployments and for customers willing to use Microsoft as carrier of record where Calling Plans are sold. Simplest provisioning; no operator or SBC dependency. Limited by Microsoft's Calling Plan country availability and by procurement policies that mandate the customer's existing carrier.
- **Operator Connect.** Recommended default where the customer's existing operator has an OC offering. Keeps the carrier relationship intact, no SBC to operate, provisioning via Teams admin center. Limited to operators that have onboarded to the OC program in the relevant region.
- **Direct Routing into Teams.** Recommended when the customer mandates a specific carrier, requires SBC-mediated compliance recording on the PSTN leg, or operates in a region without Calling Plan or OC coverage. Customer (or partner) owns the SBC fleet, certificates, SIP trunks, and codec interop. Note this is DR *into Teams*; the Teams → ACS hop is unchanged.

Out of scope for this platform (explicitly not supported):

- **Native ACS DID (no Teams).** Out of scope — the platform is built around a Teams-anchored DID and an RA→ACS binding. Customers with no Teams footprint should evaluate a different reference architecture.
- **Direct Routing via SBC bridged directly into ACS** (bypassing Teams). Out of scope — adds an SBC fleet, SIP/SRTP operations, and codec interop work that TPE eliminates, and breaks the assumption that Teams owns the PSTN edge.
- **Auto Attendant / Call Queue handing off to a bot.** Out of scope — adds an extra prompt layer the AI agent should own itself, and the bot framework call-handling surface is narrower than ACS Call Automation (no built-in `Recognize`/`Play` verbs against the same identity).
