using System.Text.Json;
using Agents.AI.ContactCenter.Calling.Core;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using ContactCenter.AIAgent.Configuration;
using ContactCenter.AIAgent.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ContactCenter.AIAgent.Endpoints;

/// <summary>Event Grid synchronous subscription-validation response.</summary>
public sealed record SubscriptionValidationResponse(string ValidationResponse);
/// <summary>Public, non-sensitive description of the demo's operating mode.</summary>
public sealed record DemoStatusResponse(string Mode, string Workflow, bool UsesDemoBankingData);

public static class CallEndpoints
{
    public static void MapBankingDemo(this WebApplication app, BankingDemoOptions options)
    {
        app.MapGet("/demo", (CancellationToken _) => TypedResults.Ok(new DemoStatusResponse(
            options.Enabled ? "live-connectors" : "configuration-preview", CallCoordinator.WorkflowId, true)))
            .WithName("GetDemoStatus").WithSummary("Describe the demo without exposing customers, credentials, or active calls.");

        if (!options.Enabled)
        {
            app.MapPost("/events/incoming-call", (CancellationToken _) => TypedResults.Problem(statusCode: 503, title: "Live calling is not configured."));
            app.MapPost("/automation/callbacks/{routeId}", (CancellationToken _) => TypedResults.Problem(statusCode: 503, title: "Live calling is not configured."));
            app.MapGet("/automation/media/{routeId}", (CancellationToken _) => TypedResults.Problem(statusCode: 503, title: "Live calling is not configured."));
            return;
        }
        app.MapPost("/events/incoming-call", IncomingAsync)
            .WithName("ReceiveIncomingCall").WithSummary("Validate an Event Grid subscription or answer an authenticated incoming call.")
            .WithDescription("Event Grid event schema. The harmless validation echo accepts only a single validation event. Call delivery requires the configured Entra sender.")
            .Accepts<JsonElement[]>("application/json").Produces<SubscriptionValidationResponse>().Produces(200).ProducesProblem(401).ProducesProblem(403);
        app.MapPost("/automation/callbacks/{routeId}", CallbackAsync)
            .RequireAuthorization(WebhookAuthentication.AcsScheme)
            .WithName("ReceiveCallCallback").WithSummary("Process authenticated ACS lifecycle callbacks.")
            .Accepts<JsonElement[]>("application/json").Produces(200).ProducesProblem(401);
        app.MapGet("/automation/media/{routeId}", MediaAsync)
            .RequireAuthorization(WebhookAuthentication.AcsScheme)
            .WithName("ConnectCallMedia").WithSummary("Upgrade an authenticated ACS call-media WebSocket.")
            .Produces(101).ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
    }

    private static async Task<IResult> IncomingAsync(HttpContext http, IAuthorizationService authorization, ICallCoordinator calls,
        CancellationToken cancellationToken)
    {
        var payload = await BinaryData.FromStreamAsync(http.Request.Body, cancellationToken).ConfigureAwait(false);
        var events = EventGridEvent.ParseMany(payload);
        if (events.Length is < 1 or > 16) { return TypedResults.Problem(statusCode: 400, title: "Invalid event batch size."); }
        if (events.Length == 1 && events[0].EventType == "Microsoft.EventGrid.SubscriptionValidationEvent")
        {
            var validation = events[0].Data.ToObjectFromJson<SubscriptionValidationEventData>();
            return validation?.ValidationCode is { Length: > 0 and <= 256 } code
                ? TypedResults.Ok(new SubscriptionValidationResponse(code))
                : TypedResults.Problem(statusCode: 400, title: "Invalid subscription validation.");
        }
        var authenticated = await http.AuthenticateAsync(WebhookAuthentication.EventGridScheme).ConfigureAwait(false);
        if (!authenticated.Succeeded || authenticated.Principal is null)
        {
            return TypedResults.Problem(statusCode: 401, title: "Event Grid authentication is required.");
        }
        if (!(await authorization.AuthorizeAsync(authenticated.Principal, null, WebhookAuthentication.EventGridPolicy).ConfigureAwait(false)).Succeeded)
        {
            return TypedResults.Problem(statusCode: 403, title: "Event Grid sender is not allowed.");
        }
        foreach (var envelope in events)
        {
            if (envelope.EventType != "Microsoft.Communication.IncomingCall") { continue; }
            if (envelope.EventTime < DateTimeOffset.UtcNow.AddSeconds(-60) || envelope.EventTime > DateTimeOffset.UtcNow.AddSeconds(30)) { continue; }
            var incoming = envelope.Data.ToObjectFromJson<AcsIncomingCallEventData>()
                ?? throw new JsonException("Missing call event data.");
            if (string.IsNullOrWhiteSpace(envelope.Id) || string.IsNullOrWhiteSpace(incoming.ServerCallId)
                || string.IsNullOrWhiteSpace(incoming.IncomingCallContext) || incoming.FromCommunicationIdentifier is null
                || incoming.ToCommunicationIdentifier is null) { return TypedResults.Problem(statusCode: 400, title: "Incomplete call event."); }
            await calls.ReceiveAsync(new(envelope.Id, incoming.IncomingCallContext, incoming.ToCallInfo()), cancellationToken).ConfigureAwait(false);
        }
        return TypedResults.Ok();
    }

    private static async Task<Ok> CallbackAsync(string routeId, HttpContext http, ICallCoordinator calls, CancellationToken cancellationToken)
    {
        var data = await BinaryData.FromStreamAsync(http.Request.Body, cancellationToken).ConfigureAwait(false);
        var events = CloudEvent.ParseMany(data);
        if (events.Length is < 1 or > 32 || events.Any(e => string.IsNullOrWhiteSpace(e.Id)))
        {
            throw new BadHttpRequestException("Invalid callback batch.");
        }
        await calls.CallbackAsync(routeId, events, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok();
    }

    private static async Task MediaAsync(string routeId, HttpContext http, ICallCoordinator calls, CancellationToken cancellationToken)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await TypedResults.Problem(statusCode: 400, title: "A WebSocket upgrade is required.").ExecuteAsync(http);
            return;
        }
        if (!calls.HasRoute(routeId))
        {
            await TypedResults.Problem(statusCode: 404, title: "Call media route not found.").ExecuteAsync(http);
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await calls.RunMediaAsync(routeId, socket, cancellationToken).ConfigureAwait(false);
    }
}
