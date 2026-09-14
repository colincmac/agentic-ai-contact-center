# Runbook: Dynamics and Power Platform telemetry export recovery

- **Status:** draft; tenant configuration and cutover not exercised
- **Last reviewed:** 2026-09-14
- **Owning team/role:** Power Platform/Dynamics administrator and observability on-call
- **Related ADRs:** [ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md) (proposed)

## Purpose

Recover the configured Dynamics 365 Contact Center or Power Platform export to
Application Insights, and explicitly track any delayed/missing conversation
evidence. An application App Configuration change cannot retarget a Microsoft
managed SaaS export.

## Triggers

- `PowerPlatformExportDelayed`: no expected synthetic conversation/export record
  after the tenant's documented baseline plus approved tolerance.
- Export configuration reports a failure, points to the wrong destination, or
  its Application Insights/LAW query path is unavailable.
- An approved SaaS export recovery drill.

Do not reuse a five-minute application telemetry threshold for asynchronous SaaS
exports. Missing records are not proof that the contact-center workload failed.
Compare source-side conversation/routing records with the expected export.
The [export setup guide](https://learn.microsoft.com/en-us/power-platform/admin/set-up-export-application-insights)
states a 24-hour telemetry-delivery SLA and up to 24 hours for initial flow.
The Dynamics dashboard's separate 15-minute display-delay guidance is not a
five-minute ingestion guarantee or a failover RTO.

## Prerequisites

- Identify the **exact export type**: Dynamics conversation diagnostics, Dataverse
  diagnostics/performance export, or another feature-specific integration.
- Tenant/environment eligibility, licenses/Managed Environment requirements and
  administrator roles verified against that product's current documentation.
  The general export guide requires tenant Power Platform/Dynamics admin
  privileges **and** environment/system administrator privileges, plus the
  appropriate Azure destination permissions.
- Saved current export configuration, event-category selections, tenant/environment
  identity in approved private inventory, and owner-approved recovery procedure.
- Alternate Application Insights component and independent LAW in an approved
  geography, with supported authentication/network configuration and query access.
  The documented Power Platform integration requires **local authentication**;
  do not assume an Entra-only component works. Obtain security approval for a
  dedicated SaaS component and test its ingestion reachability rather than
  weakening all application components.
- One Customer Service export configuration per environment, and an
  environment-specific Application Insights destination. Verify commercial/
  sovereign-cloud eligibility and feature status before proceeding.
- A non-sensitive test conversation/work item, expected identifiers, time windows,
  and access to source-side diagnostics.
- Review the relevant procedures before changing configuration:
  [Dynamics conversation diagnostics](https://learn.microsoft.com/en-us/dynamics365/customer-service/administer/configure-conversation-diagnostics),
  [Power Platform conversation export](https://learn.microsoft.com/en-us/power-platform/admin/conversation-diagnostics-application-insights),
  [Power Platform export administration](https://learn.microsoft.com/en-us/power-platform/admin/set-up-export-application-insights),
  and [Power Platform integration overview](https://learn.microsoft.com/en-us/power-platform/admin/overview-integration-application-insights).

## Safeguards

- Do not assume the export is dual-target, editable in place, replayable, or
  available through a supported public automation API.
  Reviewed guidance documents create/delete, not in-place destination failover.
  Do not substitute App Center export APIs or classic AI continuous export.
- Do not delete an export definition in production until the product's supported
  replacement procedure, permissions, interruption risk, and rollback have been
  verified and approved.
- Do not change contact routing or terminate conversations merely to diagnose
  export. Limit test activity to the agreed synthetic scenario.
- Preserve old Application Insights/LAW data and query permissions. Retargeting
  does not move history or reconstruct missing transfers.
- Do not put actual environment IDs, customer conversation contents, connection
  strings, or screenshots exposing them into public repository evidence.
- LAW replication is not a validated end-to-end Application Insights/SaaS export
  recovery mechanism under the current support matrix.

## Diagnosis

1. Confirm a test conversation/work item exists at the source, and record its
   lifecycle/event times and sanitized correlation identifiers.
   **Expected:** evidence the event should be eligible for the configured export.
2. Confirm the selected export categories, environment, target subscription/
   resource, export status, and any platform-side sampling/eligibility rules.
   **Expected:** the exact producer/export path is known.
3. Query both the Application Insights component and its backing LAW. Account for
   `traces`/`customDimensions` versus `AppTraces`/`Properties` query surfaces.
   **Expected:** export failure is separated from schema/query/RBAC mistakes.
4. Compare elapsed time to the product-specific configuration and delivery
   latency, then inspect Azure/Power Platform service health and target limits.
   **Expected:** a justified incident rather than premature repeated reconfiguration.

## Mitigation

1. If only the dashboard or query identity failed, restore that access without
   changing the export.
   **Expected:** existing data becomes queryable and healthy ingestion is preserved.
2. If the destination is impaired, have the authorized SaaS administrator open the
   Power Platform admin center: **Manage > Data export > App Insights**.
   Validate the alternate and save the previous selection/category settings.
   **Expected:** a verified supported change path for that exact export type.
3. Use the approved replacement procedure documented by the export guide:
   select **only the affected package > Delete export**, then **New data export**.
   Choose **Dynamics Customer Service** for conversation diagnostics, or
   **Dataverse diagnostics and performance** for that separate export. Restore
   its environment/filter selections, select the alternate subscription/resource
   group/Application Insights, review and **Create**.
   If the exact product/tenant workflow or replacement cannot be verified, stop
   and escalate **before deletion**, rather than scripting private endpoints or
   browser clicks as unattended DR. A documented in-place API can replace this
   baseline only after a separate support and rollback validation.
   **Expected:** read-back identifies the intended environment and alternate
   Application Insights component; any export interruption is timestamped.
4. Generate a new eligible synthetic conversation/work item **after** the new
   configuration is active. Allow the documented up-to-24-hour initial flow
   interval and monitor against the 24-hour delivery SLA; these are not a
   guarantee of recovery within 24 hours of the original incident.
   **Expected:** the new record appears at the alternate; absence during the
   expected delay is not misreported as another failure.
5. Verify documented identifiers and event categories, and update authorized
   correlation queries/dashboard scopes across old/new workspaces.
   **Expected:** a test call can be investigated without hiding the cutover gap.
6. Record late arrivals, missing intervals and possible duplicates. Keep the old
   target accessible; do not assume export buffering automatically replays all
   records into the new target.
   **Expected:** an explicit completeness statement for the incident.

## Verification

- Verify fresh post-change source events at the alternate and preserve UTC source
  versus ingestion timestamps.
- Confirm environment/work-item/conversation identifiers and required category
  coverage. Validate transferred/consult scenarios separately; do not infer their
  coverage from the product dashboard.
- Reconstruct the test call with application and ACS evidence after allowing the
  SaaS export delay. Mark unsupported/missing context fields as gaps.
- Confirm actual operator access, dashboards and delayed-export notifications.
- Report both configuration completion and measured data recovery time.

## Rollback

Use the same **supported** administration procedure to restore the prior export
destination, with approval and another activation/delivery observation window.
If replacement rather than editing is required, follow the approved replacement
sequence again; do not assume a reversible one-click toggle.
Keep both destinations queryable and reconcile cutover intervals. Do not delete
resources or change retention as rollback.

## Escalation

Escalate unsupported edits, missing roles/eligibility, or persistent delayed export
to the tenant Power Platform/Dynamics administrator and Microsoft product support.
Escalate target ingestion/query failure to Azure support. Notify the incident
commander when the approved evidence-loss or recovery window is exceeded.
If no telemetry appears within 24 hours of setup, follow the export guide's
checks for administrator permissions and the correct instrumentation key before
recreating the package again.

## Evidence capture

Use the [evidence template](../../evidence/templates/evidence-record.template.md).
Record export type, product documentation date, eligibility/roles checked,
sanitized before/after configuration, category selections, approval, activation
delay, test conversation timestamps, observed delivery lag, gaps/duplicates,
query coverage and rollback. Label unsupported automation and untested export
types as **gap**.

## Related ADRs

[ADR-0017](../../adr/0017-telemetry-high-availability-and-disaster-recovery.md)
proposes source-specific recovery and manual SaaS administration as the baseline
until supported automation is verified.
