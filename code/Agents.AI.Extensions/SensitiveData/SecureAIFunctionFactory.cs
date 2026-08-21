using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.SensitiveData;

public static class SecureAIFunctionFactory
{
    public static AIFunction CreateSecure(Delegate function, JsonSerializerOptions jsonSerializerOptions)
    {
        var method = function.Method;
        var parameters = method.GetParameters();
        var sensitiveParams = new HashSet<string>();

        foreach (var param in parameters)
        {
            var sensitiveAttr = param.GetCustomAttribute<SensitiveParameterAttribute>();
            if (sensitiveAttr is not null && !string.IsNullOrEmpty(param.Name))
            {
                sensitiveParams.Add(param.Name);
            }
        }

        var protectResult = method.GetCustomAttribute<SensitiveResultAttribute>() is not null;

        var baseFunction = AIFunctionFactory.Create(function, serializerOptions: jsonSerializerOptions);

        return new SecureDataFunction(
            baseFunction,
            sensitiveParams,
            protectResult);
    }
}
