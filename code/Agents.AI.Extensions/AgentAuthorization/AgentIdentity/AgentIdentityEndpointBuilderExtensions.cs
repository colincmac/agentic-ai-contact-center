using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace Agents.AI.Extensions.AgentAuthorization.AgentIdentity;

public static class AgentIdentityEndpointBuilderExtensions
{

    public static void MapAgentIdentityManagement(this IEndpointRouteBuilder endpoints, string downstreamServiceName = "agent-identity", [StringSyntax("Route")] string path = "identity")
    {
        var routeGroup = endpoints.MapGroup(path);

        var create = routeGroup.MapPost("/agentidentity", async ([FromBody] AgentIdentityGraphModel agentIdentity, [FromQuery] string? agentUserIdentityUpn, [FromServices] IDownstreamApi downstreamApi, CancellationToken cancellationToken) =>
        {
            var newAgentIdentity = await downstreamApi.PostForAppAsync<AgentIdentityGraphModel, AgentIdentityGraphModel>(downstreamServiceName, agentIdentity, cancellationToken: cancellationToken);
            AgentUserIdentity? newAgentUserId = null;

            if (!string.IsNullOrEmpty(agentUserIdentityUpn) && newAgentIdentity is not null)
            {
                newAgentUserId = await downstreamApi.PostForAppAsync<AgentUserIdentity, AgentUserIdentity>(
                   downstreamServiceName,
                   new AgentUserIdentity
                   {
                       displayName = agentUserIdentityUpn[..agentUserIdentityUpn.IndexOf('@', StringComparison.Ordinal)],
                       mailNickname = agentUserIdentityUpn[..agentUserIdentityUpn.IndexOf('@', StringComparison.Ordinal)],
                       userPrincipalName = agentUserIdentityUpn,
                       accountEnabled = true,
                       identityParentId = newAgentIdentity.id
                   },
                   options =>
                   {
                       options.RelativePath = "/beta/users";
                   });
            }



            return Results.Ok(new { AgentIdentity = newAgentIdentity, AgentUserIdentity = newAgentUserId });
        });


        var addUpn = routeGroup.MapPost("/agentidentity/{agentId}/userIdentity", async([FromRoute] string agentId, [FromBody] AgentUserIdentityRequest agentUserIdentity, [FromServices] IDownstreamApi downstreamApi, CancellationToken cancellationToken) =>
        {
            var existingIdentity = await downstreamApi.GetForAppAsync<AgentIdentityGraphModel>(downstreamServiceName, opt =>
            {
                opt.RelativePath += $"/{agentId}";
                
            }, cancellationToken: cancellationToken);


            if (existingIdentity is null || string.IsNullOrEmpty(existingIdentity.id)) return Results.NotFound($"Agent Identity with ID {agentId} not found.");

            var newAgentUserId = await downstreamApi.PostForAppAsync<AgentUserIdentity, AgentUserIdentity>(
               downstreamServiceName,
               new AgentUserIdentity
               {
                     displayName = agentUserIdentity.displayName,
                     mailNickname = agentUserIdentity.mailNickname,
                     userPrincipalName = agentUserIdentity.userPrincipalName,
                     accountEnabled = true,
                     identityParentId = existingIdentity.id
               },
               options =>
               {
                   options.RelativePath = "/beta/users";
               });



            return Results.Ok(new { AgentIdentity = existingIdentity, AgentUserIdentity = newAgentUserId });
        });

        var update = routeGroup.MapPatch("/agentidentity", async ([FromBody] AgentIdentityGraphModel agentIdentity, [FromQuery] string? agentUserIdentityUpn, [FromServices] IDownstreamApi downstreamApi, CancellationToken cancellationToken) =>
        {
            var newAgentIdentity = await downstreamApi.PostForAppAsync<AgentIdentityGraphModel, AgentIdentityGraphModel>(downstreamServiceName, agentIdentity, cancellationToken: cancellationToken);
            AgentUserIdentity? newAgentUserId = null;

            if (!string.IsNullOrEmpty(agentUserIdentityUpn) && newAgentIdentity is not null)
            {
                newAgentUserId = await downstreamApi.PostForAppAsync<AgentUserIdentity, AgentUserIdentity>(
                   downstreamServiceName,
                   new AgentUserIdentity
                   {
                       displayName = agentUserIdentityUpn[..agentUserIdentityUpn.IndexOf('@', StringComparison.Ordinal)],
                       mailNickname = agentUserIdentityUpn[..agentUserIdentityUpn.IndexOf('@', StringComparison.Ordinal)],
                       userPrincipalName = agentUserIdentityUpn,
                       accountEnabled = true,
                       identityParentId = newAgentIdentity.id
                   },
                   options =>
                   {
                       options.RelativePath = "/beta/users";
                   });
            }



            return Results.Ok(new { AgentIdentity = newAgentIdentity, AgentUserIdentity = newAgentUserId });
        });

        var delete = routeGroup.MapGet("/agentidentity/{agentId}", async ([FromServices] IDownstreamApi downstreamApi, [FromRoute] string agentId, CancellationToken cancellationToken) =>
        {
            // Call the downstream API with a DELETE request to remove an Agent Identity
            var jsonResult = await downstreamApi.DeleteForAppAsync<string?, string>(
                downstreamServiceName,
                null,
                options =>
                {
                    options.RelativePath += $"/{agentId}"; // Specify the ID of the agent identity to delete
                }, cancellationToken);
            return jsonResult;
        });
    }

    public static void MapAgentIdentityAuthorize(this IEndpointRouteBuilder endpoints, string downstreamServiceName = "AgentIdentity", [StringSyntax("Route")] string path = "identity")
    {
        var routeGroup = endpoints.MapGroup(path);

        routeGroup.MapGet("/agent-obo-user", async ([FromQuery] string agentId, [FromServices] IAuthorizationHeaderProvider authorizationHeaderProvider) =>
        {
            // Get the service to call the downstream API (preconfigured in the appsettings.json file)
            AuthorizationHeaderProviderOptions options = new AuthorizationHeaderProviderOptions().WithAgentIdentity(agentId);

            // Request user token for the agent identity
            string authorizationHeaderWithUserToken = await authorizationHeaderProvider.CreateAuthorizationHeaderForUserAsync(["https://graph.microsoft.com/.default"], options);

            var response = new { header = authorizationHeaderWithUserToken };
            return Results.Json(response);
        })
        .RequireAuthorization();
    }
}
