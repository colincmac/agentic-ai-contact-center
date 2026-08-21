# Teams Phone Extensibility (TPE)

**Teams Phone Extensibility (TPE)** is the Microsoft mechanism that delegates inbound PSTN calls landing on a Teams Phone resource account to an Azure Communication Services (ACS) Call Automation app — without an SBC, without SIP trunking in your application, and without going through Microsoft Graph. Calls flow PSTN → Teams Phone (RA) → ACS, and your app receives an `IncomingCall` event over Event Grid and answers via the Call Automation REST/SDK surface.

The full Microsoft Learn quickstart that this file used to mirror lives at:

- [Teams Phone Extensibility — Microsoft Learn](https://learn.microsoft.com/azure/communication-services/concepts/interop/teams-phone-extensibility/teams-phone-extensibility)

For the operator-side workflow in this repo, prefer:

- [`tpe-onboarding-guide.md`](tpe-onboarding-guide.md) — greenfield enterprise onboarding (provisioning the Entra app, resource account, ACS binding, Bot Service registration, and TPE configuration end-to-end).
- [`tpe-brownfield.md`](tpe-brownfield.md) — connecting an *existing* Teams Phone resource account to an *existing* ACS resource (the common case for customers who already have Teams Phone Standard or Direct Routing).
- [`ADR-0001`](../../adr/0001-pstn-ingress-via-tpe.md) — the architecture decision that standardizes on TPE while allowing Calling Plan, Operator Connect, or Direct Routing to supply the Teams resource account's number.

## Known Issues
To transfer directly to the Teams Resource Account, it needs a Phone Number
await callConnection.TransferCallToParticipantAsync(
    new TransferToParticipantOptions(target)
    {
        OperationContext = "transfer-to-dynamics"
    });
