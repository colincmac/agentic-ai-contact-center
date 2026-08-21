using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Utilities;

internal sealed class EmptyServiceProvider : IKeyedServiceProvider
{
    /// <summary>Gets a singleton instance of <see cref="EmptyServiceProvider"/>.</summary>
    public static EmptyServiceProvider Instance { get; } = new();

    /// <inheritdoc />
    public object? GetService(Type serviceType) => null;

    /// <inheritdoc />
    public object? GetKeyedService(Type serviceType, object? serviceKey) => null;

    /// <inheritdoc />
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
        GetKeyedService(serviceType, serviceKey) ??
        throw new InvalidOperationException($"No service for type '{serviceType}' and key '{serviceKey}' has been registered.");
}
