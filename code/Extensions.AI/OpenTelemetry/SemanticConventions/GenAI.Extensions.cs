namespace Extensions.AI.OpenTelemetry.SemanticConventions;

public static partial class GenAI
{

    public static class EnablementSwitches
    {
        public const string AzureEnableOpenTelemetrySwitch = "Azure.Experimental.EnableActivitySource";

        public const string AzureTraceContentsSwitch = "Azure.Experimental.TraceGenAIMessageContent";
        public const string AzureTraceContentsEnvironmentVariable = "AZURE_TRACING_GEN_AI_CONTENT_RECORDING_ENABLED";
        public const string AzureEnableOpenTelemetryEnvironmentVariable = "AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE";
        /// <summary>Environment variable name for controlling whether sensitive content should be captured in telemetry by default.</summary>
        public const string GenAICaptureMessageContentEnvVar = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";
    }

    public const string SecondsUnit = "s";
    public const string TokensUnit = "token";



    public const string ToolTypeFunction = "function";

    public const string TypeText = "text";
    public const string TypeJson = "json";

    public const string TokenTypeInput = "input";
    public const string TokenTypeOutput = "output";

    public static partial class OperationNameValues
    {
        public const string OrchestrateToolsName = "orchestrate_tools"; // Non-standard
        public const string ChatName = "chat";
        public const string EmbeddingsName = "embeddings";
        public const string ExecuteToolName = "execute_tool";
        public const string InvokeAgentName = "invoke_agent";
        public const string GenerateContentName = "generate_content";
    }
    public static class Tool
    {
        public const string Name = "gen_ai.tool.name";
        public const string Description = "gen_ai.tool.description";
        public const string Message = "gen_ai.tool.message";
        public const string Type = "gen_ai.tool.type";
        public const string Definitions = "gen_ai.tool.definitions";

        public static class Call
        {
            public const string Id = "gen_ai.tool.call.id";
            public const string Arguments = "gen_ai.tool.call.arguments";
            public const string Result = "gen_ai.tool.call.result";
        }
    }

    public static class Usage
    {
        public const string InputTokens = "gen_ai.usage.input_tokens";
        public const string OutputTokens = "gen_ai.usage.output_tokens";
        public const string CacheReadInputTokens = "gen_ai.usage.cache_read.input_tokens";
        public const string InputAudioTokens = "gen_ai.usage.input_audio_tokens";
        public const string InputTextTokens = "gen_ai.usage.input_text_tokens";
        public const string OutputAudioTokens = "gen_ai.usage.output_audio_tokens";
        public const string OutputTextTokens = "gen_ai.usage.output_text_tokens";
    }

    public static class Embeddings
    {
        public static class Dimension
        {
            public const string Count = "gen_ai.embeddings.dimension.count";
        }
    }

    public static class Client
    {
        public static class OperationDuration
        {
            public const string Description = "Measures the duration of a GenAI operation";
            public const string Name = "gen_ai.client.operation.duration";
            public static readonly double[] ExplicitBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92];
        }

        public static class TokenUsage
        {
            public const string Description = "Measures number of input and output tokens used";
            public const string Name = "gen_ai.client.token.usage";
            public static readonly int[] ExplicitBucketBoundaries = [1, 4, 16, 64, 256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576, 4_194_304, 16_777_216, 67_108_864];
        }
    }
}
