using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.AI.Extensions.SensitiveData;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.Tests;


public partial class SecureDataFunctionTests
{
    public const string AccountNumber = "user-123";
    public const string RoutingNumber = "1111-1111-1111-1111";


    [Fact]
    public async Task SecureDataFunction_ObfuscatesParametersAndResult_WithSensitiveDataReferenceParameters()
    {
        ServiceCollection sc = new();
        var dataStore = new InMemoryFunctionContextProvider();

        var accountNumberKey = await dataStore.SetAsync(AccountNumber, TestContext.Current.CancellationToken);
        var routingNumberKey = await dataStore.SetAsync(RoutingNumber, TestContext.Current.CancellationToken);
        var functionParams = $"{{ \"accountNumber\": {{ \"$ref\": \"{accountNumberKey}\" }}, \"routingNumber\": {{ \"$ref\": \"{routingNumberKey}\" }} }}";

        sc.AddSingleton<IFunctionContextProvider>(dataStore);
        IServiceProvider sp = sc.BuildServiceProvider();

        var originalFunction = AIFunctionFactory.Create(
            BasicSensitiveFunction, serializerOptions: TestJsonSerializerContext.Default.Options);

        var protectedFunction = new SecureDataFunction(
            originalFunction,
            sensitiveParameterNames: new HashSet<string> { "accountNumber", "routingNumber" },
            protectReturnValue: true);

        var content = FunctionCallContent.CreateFromParsedArguments(
            functionParams,
            "callid1",
            nameof(BasicSensitiveFunction),
            argumentParser: static json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions));

        var arguments = new AIFunctionArguments(content.Arguments) { Services = sp };

        // The interceptor resolves references and returns a reference
        var result = await protectedFunction.InvokeAsync(arguments, TestContext.Current.CancellationToken);

        Assert.IsType<SensitiveDataReference>(result);

        var resultReferenceId = (result as SensitiveDataReference)?.ReferenceToken;
        var resultValue = await dataStore.GetAsync<SensitiveEntity>(resultReferenceId!, TestContext.Current.CancellationToken);
        Assert.IsType<SensitiveEntity>(resultValue);

        Assert.Equal(AccountNumber, resultValue?.AccountNumber);
        Assert.Equal(RoutingNumber, resultValue?.RoutingNumber);
    }

    public class SensitiveEntity
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string RoutingNumber { get; set; } = string.Empty;
    }

    public Task<SensitiveEntity> BasicSensitiveFunction([Description("The account number of the account.")] string accountNumber, [Description("The routing number of the account.")] string routingNumber)
    {
        return Task.FromResult(new SensitiveEntity
        {
            AccountNumber = accountNumber,
            RoutingNumber = routingNumber
        });
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(SensitiveEntity))]
    public partial class TestJsonSerializerContext : JsonSerializerContext
    {
    }
}
