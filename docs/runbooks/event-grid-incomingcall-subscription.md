# Runbook: Subscribing to `Microsoft.Communication.IncomingCall` via Event Grid

This is the **one-time bootstrap** that has to succeed before your IVR can ever receive a call. When you (or your IaC) first create — or update — the Event Grid subscription that points at your IVR's `IncomingCall` webhook, Event Grid will not deliver real events until your endpoint proves it owns the URL by completing the validation handshake.

Two modes are supported:

- **Synchronous** — return the validation code in the HTTP 200 body. Preferred. Simpler, less error-prone, the default for first-party Microsoft endpoints.
- **Asynchronous** — call back a one-time validation URL within 5 minutes. Required when your endpoint is fronted by SSO / a proxy / cross-tenant policies that block the Event Grid validation request from reaching it.

Both flows are shown below.

```mermaid
sequenceDiagram
    autonumber
    actor Operator as Operator / IaC<br/>(Bicep / Terraform / Portal)
    participant ARM as Azure Resource Manager
    participant EG as Azure Event Grid<br/>(System Topic on ACS resource)
    participant IVR as IVR App (AKS)<br/>HTTPS webhook
    participant ACS as ACS Calling Backend

    %% ───────────── Subscription creation ─────────────
    rect rgb(235, 245, 255)
    Note over Operator,EG: 1. Create the Event Grid subscription
    Operator->>ARM: PUT eventSubscription<br/>{topic = ACS resource,<br/> filter = Microsoft.Communication.IncomingCall,<br/> endpoint = https://ivr.contoso.com/events/incoming-call}
    ARM->>EG: Provision subscription (Provisioning = "Creating")
    end

    %% ───────────── Validation request ─────────────
    rect rgb(255, 250, 230)
    Note over EG,IVR: 2. Validation request (always sent first)
    EG->>IVR: POST /events/incoming-call<br/>aeg-event-type: SubscriptionValidation<br/>[{ eventType: "Microsoft.EventGrid.SubscriptionValidationEvent",<br/>   data: { validationCode: "<GUID>",<br/>           validationUrl: "https://...?id=<GUID>&token=..." } }]
    end

    %% ───────────── Two response modes ─────────────
    alt Synchronous handshake (preferred)
        rect rgb(240, 255, 240)
        Note over IVR,EG: 2a. Echo validationCode in the HTTP 200 body
        IVR->>IVR: Detect SubscriptionValidationEvent<br/>Extract validationCode
        IVR-->>EG: 200 OK<br/>{ "validationResponse": "<GUID>" }
        EG->>EG: Compare echoed code → match
        end
    else Asynchronous handshake (cross-tenant / proxied / SSO endpoints)
        rect rgb(255, 240, 245)
        Note over IVR,EG: 2b. Out-of-band GET to validationUrl within 5 minutes
        IVR-->>EG: 200 OK (no validationResponse body)
        Note over IVR: Manual / scripted approval step<br/>(human reviews the URL before confirming)
        IVR->>EG: GET validationUrl<br/>(must occur within 5 min, else subscription fails)
        EG->>EG: Mark validation complete
        end
    end

    %% ───────────── Activation ─────────────
    rect rgb(245, 245, 255)
    Note over EG,ARM: 3. Subscription becomes active
    EG-->>ARM: Provisioning = "Succeeded"
    ARM-->>Operator: Subscription created
    end

    %% ───────────── First real event ─────────────
    rect rgb(230, 255, 240)
    Note over ACS,IVR: 4. From now on, real IncomingCall events flow
    ACS->>EG: Publish Microsoft.Communication.IncomingCall
    EG->>IVR: POST /events/incoming-call<br/>(real CloudEvent — no validation envelope)
    IVR-->>EG: 200 OK
    end

    %% ───────────── Failure path ─────────────
    rect rgb(255, 235, 235)
    Note over EG,IVR: Failure mode — endpoint silent or wrong code
    Note over EG: Event Grid retries validation with exponential backoff<br/>over ~24h. If still unvalidated → Provisioning = "Failed".<br/>No events are delivered until the subscription is recreated.
    end
```

## Key things to bake into your handler

- **Detect the validation envelope first** — check `eventType == "Microsoft.EventGrid.SubscriptionValidationEvent"` (or header `aeg-event-type: SubscriptionValidation`) before treating the payload as a real event.
- **Return the code synchronously** when you can; it's simpler and less error-prone than the async URL flow.
- **Don't require auth on the validation request itself** — Event Grid won't carry your bearer token. Use a hard-to-guess URL path + payload validation, or front the endpoint with Event Grid's built-in delivery options (managed identity / WebHook secret).
- **Log the `validationCode`** so that if you ever need the async path, you can hit the URL manually within the 5-minute window.

## Related

- [ADR-0003 — `IncomingCall` delivery via Event Grid](../adr/0003-incomingcall-delivery-via-event-grid.md) for the *why*.
- [`../architecture/call-flow.md`](../architecture/call-flow.md) §3 for where this subscription fits into the runtime call flow.
- [`timing-and-retries.md`](timing-and-retries.md) for the Event Grid retry/TTL schedule once the subscription is live.
