using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Agents.AI.Sandbox;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.Sandbox.Tests;

public sealed class SandboxedAIFunctionTests
{
    private static CallSandboxOptions EnabledOptions() => new()
    {
        Enabled = true,
        SubscriptionId = "sub",
        ResourceGroup = "rg",
        SandboxGroup = "sg",
        Credential = new FakeSandboxTokenCredential(),
    };

    private static CallSandboxManager Manager(SandboxRecordingHandler handler, CallSandboxOptions options) => new(
        new SingleClientHttpClientFactory(handler),
        Options.Create(options),
        TestTelemetry.Sandbox,
        CallSandboxManagerTests.TestCallId,
        NullLoggerFactory.Instance);

    private static IServiceProvider ServicesWith(ICallSandboxManager manager) =>
        new ServiceCollection().AddSingleton(manager).BuildServiceProvider();

    [Fact]
    public void TryWrap_ReturnsNull_WhenNotMarked()
    {
        var function = AIFunctionFactory.Create(SampleSandboxTools.Plain);
        Assert.Null(SandboxedAIFunction.TryWrap(function, null));
    }

    [Fact]
    public void TryWrap_Wraps_WhenMarked_AndForwardsModelSurface()
    {
        var function = AIFunctionFactory.Create(SampleSandboxTools.Lookup);
        var wrapped = SandboxedAIFunction.TryWrap(function, null);

        Assert.NotNull(wrapped);
        // The model-facing surface must be identical to the original function.
        Assert.Equal(function.Name, wrapped!.Name);
        Assert.Equal(function.Description, wrapped.Description);
    }

    [Fact]
    public async Task Invoke_RoutesToSandbox_AndMarshalsArgsAsBase64_NeverRawShell()
    {
        var handler = new SandboxRecordingHandler();
        await using var manager = Manager(handler, EnabledOptions());

        var wrapped = SandboxedAIFunction.TryWrap(AIFunctionFactory.Create(SampleSandboxTools.Lookup), null)!;
        const string malicious = "'; rm -rf / ; echo '";
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = malicious })
        {
            Services = ServicesWith(manager),
        };

        var result = await wrapped.InvokeAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", result?.ToString());

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        using var doc = JsonDocument.Parse(post.Body);
        var command = doc.RootElement.GetProperty("command").GetString()!;

        // The runner is invoked with the configured entrypoint and the tool name.
        Assert.Contains("python3 /tools/run.py", command);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapped.Name)), command);
        // Arguments cross as base64 JSON — the raw injection payload never appears verbatim.
        Assert.DoesNotContain("rm -rf /", command);

        // Decode the marshaled arguments and verify fidelity (robust to JSON string escaping).
        var quoted = System.Text.RegularExpressions.Regex.Matches(command, "'([^']*)'");
        var argsJson = Encoding.UTF8.GetString(Convert.FromBase64String(quoted[^1].Groups[1].Value));
        using var argsDoc = JsonDocument.Parse(argsJson);
        Assert.Equal(malicious, argsDoc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Invoke_UsesExplicitToolName_WhenProvided()
    {
        var handler = new SandboxRecordingHandler();
        await using var manager = Manager(handler, EnabledOptions());

        var wrapped = SandboxedAIFunction.TryWrap(AIFunctionFactory.Create(SampleSandboxTools.Renamed), null)!;
        var args = new AIFunctionArguments(new Dictionary<string, object?>()) { Services = ServicesWith(manager) };

        await wrapped.InvokeAsync(args, TestContext.Current.CancellationToken);

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        using var doc = JsonDocument.Parse(post.Body);
        var command = doc.RootElement.GetProperty("command").GetString()!;
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("sandbox-side-name")), command);
    }

    [Fact]
    public async Task Invoke_FailsClosed_WhenSandboxDisabled_WithoutProvisioning()
    {
        var handler = new SandboxRecordingHandler();
        await using var manager = Manager(handler, new CallSandboxOptions { Enabled = false });

        var wrapped = SandboxedAIFunction.TryWrap(AIFunctionFactory.Create(SampleSandboxTools.Lookup), null)!;
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "x" }) { Services = ServicesWith(manager) };

        var result = await wrapped.InvokeAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal("This tool is currently unavailable.", result?.ToString());
        Assert.Empty(handler.Requests);
    }

    private static class SampleSandboxTools
    {
        [Description("A plain in-process tool.")]
        public static string Plain() => "ran-locally";

        [SandboxedTool]
        [Description("Look something up using an untrusted runner.")]
        public static string Lookup([Description("query")] string query) => "LOCAL-SHOULD-NEVER-RUN";

        [SandboxedTool("sandbox-side-name")]
        [Description("Renamed sandbox tool.")]
        public static string Renamed() => "LOCAL-SHOULD-NEVER-RUN";
    }
}
