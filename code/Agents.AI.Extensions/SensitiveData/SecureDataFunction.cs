using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.SensitiveData;


public class SecureDataFunction(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
{
    private readonly HashSet<string>? _sensitiveParams;
    private readonly bool _protectReturnValue;

    /// <summary>
    /// Supports obfuscating function parameters and return values from the AI. Allows encapsulation of sensitive data handling.
    /// Provides a wrapper for an AI function that transparently manages sensitive data parameters and return values,
    /// ensuring that sensitive information is referenced rather than directly exposed during function invocation.
    /// </summary>
    /// <remarks>Use this constructor to specify which parameters are considered sensitive and whether the
    /// return value should be protected. If sensitiveParameterNames is not provided, the class will attempt to
    /// determine sensitive parameters automatically. Protecting the return value may be important when handling
    /// confidential outputs.
    /// <br/>
    /// <br/>
    /// <b>NOTE:</b> This class requires an IFunctionContextProvider service to be available in the function arguments' services. If using <see cref="FunctionInvokingChatClient"/>, the client's services are automatically added by default.
    /// </remarks>
    /// <param name="innerFunction">The AI function to be wrapped and protected. This function will be invoked with sensitive data handling applied
    /// as configured.</param>
    /// <param name="sensitiveParameterNames">A set of parameter names that should be treated as sensitive. If null, sensitive parameters will be inferred
    /// automatically.</param>
    /// <param name="protectReturnValue">true to protect the return value as sensitive data; otherwise, false.</param>
    public SecureDataFunction(
        AIFunction innerFunction,
        HashSet<string>? sensitiveParameterNames = null,
        bool protectReturnValue = true) : this(innerFunction)
    {
        _sensitiveParams = sensitiveParameterNames ?? TryGetSensitiveParams();
        _protectReturnValue = protectReturnValue;
    }


    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        var contextProvider = arguments.Services?.GetService(typeof(IFunctionContextProvider)) as IFunctionContextProvider ?? throw new InvalidOperationException("IFunctionContextProvider service is not available in the function arguments.");

        // Dereference incoming arguments (AI sent references, we need actual values)
        var processedArgs = await ResolveArgumentsAsync(contextProvider, arguments, cancellationToken).ConfigureAwait(false);

        // Execute the actual function
        var result = await InnerFunction.InvokeAsync(processedArgs, cancellationToken).ConfigureAwait(false);

        // Reference outgoing result (convert actual value to reference for AI)

        if (_protectReturnValue && result is not null)
        {
            var referenceToken = await contextProvider.SetAsync(result, cancellationToken).ConfigureAwait(false);
            return new SensitiveDataReference(referenceToken);
        }
        return result;
    }


    private async Task<AIFunctionArguments> ResolveArgumentsAsync(IFunctionContextProvider contextProvider, AIFunctionArguments arguments, CancellationToken cancellationToken = default)
    {
        var processed = new AIFunctionArguments();
        foreach (var (paramName, paramValue) in arguments)
        {
            if ((_sensitiveParams == null
                || _sensitiveParams.Contains(paramName))
                && TryExtractReferenceToken(paramValue) is string referenceToken
                && !string.IsNullOrEmpty(referenceToken))
            {
                var actualValue = await contextProvider.GetAsync<object>(referenceToken, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Secure data with reference ID '{referenceToken}' could not be found.");

                // If the parameter is marked as sensitive but not a reference, we can choose to handle it differently
                // For now, we just pass it through
                processed.Add(paramName, actualValue);
            }
            else
            {
                processed.Add(paramName, paramValue);
            }
        }
        return processed;
    }

    private static string? TryExtractReferenceToken(object? value)
    {
        return value switch
        {
            SensitiveDataReference sdr => sdr.ReferenceToken,
            JsonElement { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty("$ref", out var refProp) => refProp.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.String } str => (str.GetString() ?? string.Empty).Substring(5),
            string stringValue => stringValue.Substring(5),
            _ => null
        };
    }

    private HashSet<string>? TryGetSensitiveParams()
    {
        return UnderlyingMethod?.GetParameters()
            .Where(p => p.GetCustomAttribute<SensitiveParameterAttribute>() != null && !string.IsNullOrEmpty(p.Name))
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
