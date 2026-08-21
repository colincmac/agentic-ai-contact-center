using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

public static class DidEndpointBuilderExtensions
{
    public static IEndpointRouteBuilder MapWellKnownDidDocument(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string path = ".well-known")
    {
        var routeGroup = endpoints.MapGroup(path);
        // Raw JSON passthrough
        routeGroup.MapGet("/did.json", ([FromServices] IOptionsMonitor<DidRawOptions> raw) =>
        {
            var doc = raw.CurrentValue.DidDocument;
            return Results.Text(doc.GetRawText(), "application/json");
        }).AllowAnonymous();

        routeGroup.MapGet("/did-configuration.json", ([FromServices] IOptionsMonitor<DidRawOptions> raw) =>
        {
            var config = raw.CurrentValue.DidConfiguration;
            return Results.Text(config.GetRawText(), "application/json");
        }).AllowAnonymous();

        return endpoints;
    }
}
