using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Configuration;

public sealed class BankingDemoOptions
{
    public const string SectionName = "BankingDemo";
    public bool Enabled { get; set; }
    public Uri? PublicBaseUri { get; set; }
    public Uri? AcsEndpoint { get; set; }
    public string AcsAudience { get; set; } = string.Empty;
    public string? AcsConnectionString { get; set; }
    public Uri? SpeechEndpoint { get; set; }
    public Uri? VoiceLiveEndpoint { get; set; }
    public string VoiceLiveModel { get; set; } = string.Empty;
    public string VoiceName { get; set; } = "en-US-AvaNeural";
    public string SmsFrom { get; set; } = string.Empty;
    public string OperatorNumber { get; set; } = string.Empty;
    public string RedirectCallerId { get; set; } = string.Empty;
    public string EventGridTenantId { get; set; } = string.Empty;
    public string EventGridAudience { get; set; } = string.Empty;
    public string EventGridObjectId { get; set; } = string.Empty;
    public string? ManagedIdentityClientId { get; set; }
    public int MediaConnectTimeoutSeconds { get; set; } = 45;
    public int MaximumCallSeconds { get; set; } = 600;
    public int OtpInputTimeoutSeconds { get; set; } = 60;
    public List<DemoCustomer> Customers { get; set; } = [];
}

public sealed class DemoCustomer
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string CardLastFour { get; set; } = "1234";
    public decimal Balance { get; set; } = 1250.50m;
}

internal sealed class BankingDemoOptionsValidator : IValidateOptions<BankingDemoOptions>
{
    public ValidateOptionsResult Validate(string? name, BankingDemoOptions options)
    {
        if (!options.Enabled) { return ValidateOptionsResult.Success; }
        var errors = new List<string>();
        foreach (var (field, uri) in new[]
        {
            (nameof(options.PublicBaseUri), options.PublicBaseUri), (nameof(options.AcsEndpoint), options.AcsEndpoint),
            (nameof(options.SpeechEndpoint), options.SpeechEndpoint), (nameof(options.VoiceLiveEndpoint), options.VoiceLiveEndpoint),
        })
        {
            if (uri is not { IsAbsoluteUri: true } || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                errors.Add($"{field} must be an absolute HTTPS URI without query/fragment.");
            }
        }
        if (options.PublicBaseUri is { IsAbsoluteUri: true } address && !address.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            errors.Add("PublicBaseUri must end with '/' so callback/media routes preserve its path prefix.");
        }
        if (!Guid.TryParse(options.EventGridTenantId, out _) || !Guid.TryParse(options.EventGridObjectId, out _))
        {
            errors.Add("Event Grid tenant and allowed sender object IDs must be configured GUIDs.");
        }
        if (string.IsNullOrWhiteSpace(options.EventGridAudience) || string.IsNullOrWhiteSpace(options.AcsAudience)
            || string.IsNullOrWhiteSpace(options.VoiceLiveModel) || string.IsNullOrWhiteSpace(options.VoiceName))
        {
            errors.Add("Webhook audiences, Voice Live model, and voice name are required.");
        }
        if (!IsPhone(options.SmsFrom) || !IsPhone(options.OperatorNumber) || !IsPhone(options.RedirectCallerId))
        {
            errors.Add("SMS sender, operator destination, and ACS-authorized redirect caller ID must be E.164 phone numbers.");
        }
        if (options.Customers.Count == 0 || options.Customers.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != options.Customers.Count
            || options.Customers.Select(c => c.PhoneNumber).Distinct(StringComparer.Ordinal).Count() != options.Customers.Count)
        {
            errors.Add("Configure at least one demo customer with unique IDs and phone numbers.");
        }
        foreach (var customer in options.Customers)
        {
            if (string.IsNullOrWhiteSpace(customer.Id) || string.IsNullOrWhiteSpace(customer.DisplayName) || !IsPhone(customer.PhoneNumber)
                || customer.CardLastFour.Length != 4 || customer.CardLastFour.Any(c => c is < '0' or > '9'))
            {
                errors.Add("Each demo customer requires an ID, display name, phone on file, and four numeric card digits.");
            }
        }
        if (options.MediaConnectTimeoutSeconds is < 5 or > 120 || options.MaximumCallSeconds is < 60 or > 3600
            || options.OtpInputTimeoutSeconds is < 1 or > 300)
        {
            errors.Add("Media timeout must be 5-120 seconds, call duration 60-3600 seconds, and OTP input timeout 1-300 seconds.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool IsPhone(string value) => value.Length is >= 9 and <= 16 && value[0] == '+'
        && value[1] is >= '1' and <= '9' && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
