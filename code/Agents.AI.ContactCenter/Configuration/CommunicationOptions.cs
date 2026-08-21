using Azure.Communication.CallAutomation;

namespace Agents.AI.ContactCenter.Configuration;

public class CommunicationOptions
{
    public const string SectionName = "Communication";
    public bool EnableOpenTelemetryInstrumentation { get; set; } = true;
    public bool ConfigureAcsTeamsIntegration { get; set; } = false;
    public required AcsOptions Acs { get; set; }
    public required TeamsOptions Teams { get; set; }

    public CallStateOptions CallState { get; set; } = new CallStateOptions();
}

public class AcsOptions
{
    public required string ConnectionString { get; set; }
    public required Uri CallBackUri { get; set; }
    public required Uri MediaStreamingUri { get; set; }
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Pcm24KMono;
    public Uri AcsResourceEndpoint => new(ConnectionString.Split(';').First(s => s.StartsWith("endpoint=", StringComparison.OrdinalIgnoreCase)).Split('=')[1]);
    public string AcsApiVersion { get; set; } = "2025-06-30";
    public Guid GlobalID { get; set; }
    public bool UseWebsocketForMediaStreaming { get; set; } = true;
};

public class TeamsOptions
{
    public required string ResourceTenantId { get; set; }
    public required string ResourceObjectId { get; set; }
    public required string PhoneNumber { get; set; }
    public string Identity { get; set; } = string.Empty;
}

