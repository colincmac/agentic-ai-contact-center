using ContactCenter.AIAgent.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ContactCenter.AIAgent.Configuration;

public static class WebhookAuthentication
{
    public const string AcsScheme = "Acs";
    public const string EventGridScheme = "EventGrid";
    public const string EventGridPolicy = "EventGridSender";

    public static IServiceCollection AddDemoWebhookAuthentication(this IServiceCollection services, BankingDemoOptions options)
    {
        services.AddAuthentication().AddJwtBearer(AcsScheme, jwt =>
        {
            jwt.MetadataAddress = "https://acscallautomation.communication.azure.com/calling/.well-known/acsopenidconfiguration";
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = Parameters(options.AcsAudience);
            jwt.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (string.IsNullOrEmpty(context.Request.Headers.Authorization))
                    {
                        var value = context.Request.Headers["Authentication"].ToString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            context.Token = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
                        }
                    }
                    return Task.CompletedTask;
                },
            };
        }).AddJwtBearer(EventGridScheme, jwt =>
        {
            jwt.Authority = $"https://login.microsoftonline.com/{options.EventGridTenantId}/v2.0";
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = Parameters(options.EventGridAudience);
            jwt.TokenValidationParameters.ValidIssuers =
            [
                $"https://sts.windows.net/{options.EventGridTenantId}/",
                $"https://login.microsoftonline.com/{options.EventGridTenantId}/v2.0",
            ];
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(AcsScheme, policy => policy.AddAuthenticationSchemes(AcsScheme).RequireAuthenticatedUser())
            .AddPolicy(EventGridPolicy, policy => policy.AddAuthenticationSchemes(EventGridScheme).RequireAuthenticatedUser()
                .RequireAssertion(context => Guid.TryParse(context.User.FindFirst("oid")?.Value, out var sender)
                    && sender == Guid.Parse(options.EventGridObjectId)));
        return services;
    }

    private static TokenValidationParameters Parameters(string audience) => new()
    {
        ValidateAudience = true, ValidAudience = audience, ValidateIssuer = true,
        ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateLifetime = true,
        RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
    };
}
