using Agents.AI.ContactCenter.Authentication;

namespace Agents.AI.ContactCenter.IvrWorkflow.Blueprint;

/// <summary>
/// Reference to a predicate evaluated when a workflow edge is traversed. The compiler
/// resolves the reference at compile time to a concrete <see cref="Predicates.EdgePredicate"/>
/// via <see cref="Predicates.BuiltInPredicates"/> or
/// <see cref="Predicates.INamedEdgePredicateProvider"/>.
/// </summary>
public sealed record PredicateRef
{
    /// <summary>The discriminator that selects the built-in factory or the named-DI path.</summary>
    public required PredicateKind Kind { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <see cref="PredicateKind.AuthLevel"/>.</summary>
    public CallerVerificationLevel? AuthLevel { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <see cref="PredicateKind.StateHas"/> or <see cref="PredicateKind.StateEquals"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <see cref="PredicateKind.StateEquals"/>; the expected value.</summary>
    public object? ExpectedValue { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <see cref="PredicateKind.Named"/>; the DI id.</summary>
    public string? NamedId { get; init; }

    /// <summary>Optional override of the default deny reason surfaced by the predicate.</summary>
    public string? FailureMessage { get; init; }

    public static PredicateRef AuthVerificationLevel(CallerVerificationLevel level, string? failureMessage = null) =>
        new() { Kind = PredicateKind.AuthLevel, AuthLevel = level, FailureMessage = failureMessage };

    public static PredicateRef StateHas(string key, string? failureMessage = null) =>
        new() { Kind = PredicateKind.StateHas, Key = key, FailureMessage = failureMessage };

    public static PredicateRef StateEquals(string key, object? expected, string? failureMessage = null) =>
        new() { Kind = PredicateKind.StateEquals, Key = key, ExpectedValue = expected, FailureMessage = failureMessage };

    public static PredicateRef Named(string id, string? failureMessage = null) =>
        new() { Kind = PredicateKind.Named, NamedId = id, FailureMessage = failureMessage };
}

public enum PredicateKind
{
    AuthLevel,
    StateHas,
    StateEquals,
    Named,
}
