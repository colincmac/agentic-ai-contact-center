using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Builds the single <c>advance</c> <see cref="AIFunction"/> exposed to the realtime model
/// per stage. Replaces the legacy <c>IvrAdvanceFunctions</c> which synthesized one
/// <c>advance_to_{stageId}</c> function per transition.
/// </summary>
/// <remarks>
/// <para>
/// The OpenAI Realtime Prompting Guide's "Dynamic Conversation Flow" pattern recommends
/// scoping tools per state and using a small, well-described tool surface. A single
/// <c>advance(target)</c> function with the valid labels enumerated in its description
/// (and rendered alongside the stage prompt) is enough for the model to drive
/// transitions without polluting the schema with N look-alike functions.
/// </para>
/// <para>
/// The function delegates to a supplied
/// <see cref="WorkflowExecutor.AdvanceToAsync(string, CancellationToken)"/>; the executor
/// owns prompt + tool re-render. The return value is shaped as a small structured
/// envelope (<see cref="AdvanceFunctionResult"/>) so the model gets a deterministic signal
/// about whether the transition landed or was denied.
/// </para>
/// </remarks>
public static class AdvanceFunctionBuilder
{
    /// <summary>Reserved function name for the advance tool. Stable so tests + telemetry can match.</summary>
    public const string FunctionName = "advance";

    /// <summary>
    /// Build the advance function for <paramref name="stage"/>, or <see langword="null"/>
    /// when the stage has no outgoing transitions (terminal or dead-end).
    /// </summary>
    public static AIFunction? BuildForStage(CompiledStage stage, WorkflowExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(executor);

        if (stage.Terminal || stage.OutgoingEdges.Count == 0)
        {
            return null;
        }

        var edgesByLabel = stage.OutgoingEdges.ToDictionary(e => e.Label, StringComparer.OrdinalIgnoreCase);

        var description = BuildDescription(stage);

        // Define the inline delegate so AIFunctionFactory.Create infers a stable JSON schema
        // for the `target` parameter (string) with a [Description] that lists allowed values.
        async Task<AdvanceFunctionResult> AdvanceAsync(
            [Description("The label of the transition to take. Pick exactly one of the values listed in the description.")]
            string target,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return AdvanceFunctionResult.Denied($"`target` is required and must be one of the listed labels. Valid labels: {string.Join(", ", edgesByLabel.Keys)}.");
            }

            if (!edgesByLabel.TryGetValue(target, out var edge))
            {
                return AdvanceFunctionResult.Denied(
                    $"'{target}' is not a valid transition label for stage '{stage.Id}'. " +
                    $"Valid labels: {string.Join(", ", edgesByLabel.Keys)}.");
            }

            // Advance along the *resolved edge* — not by target stage id — so stages with
            // multiple edges to the same target evaluate the predicate the model chose.
            var outcome = await executor.AdvanceAlongAsync(edge, cancellationToken).ConfigureAwait(false);
            return outcome switch
            {
                AdvanceOutcome.Advanced advanced => AdvanceFunctionResult.Ok(advanced.NewStage.Id),
                AdvanceOutcome.Denied denied => AdvanceFunctionResult.Denied(denied.Reason),
                AdvanceOutcome.Invalid invalid => AdvanceFunctionResult.Denied(invalid.Reason),
                _ => AdvanceFunctionResult.Denied("Unknown advance outcome."),
            };
        }

        var inner = AIFunctionFactory.Create(
            AdvanceAsync,
            new AIFunctionFactoryOptions
            {
                Name = FunctionName,
                Description = description,
            });

        // Constrain `target` to the stage's valid labels via a JSON-schema enum so a
        // compliant model can't emit an out-of-range label. The deny-envelope above stays
        // as defense-in-depth for models that ignore the constraint.
        var schema = BuildEnumConstrainedSchema(inner.JsonSchema, edgesByLabel.Keys);
        return new EnumConstrainedFunction(inner, schema);
    }

    /// <summary>
    /// Clone <paramref name="baseSchema"/> and add an <c>enum</c> of <paramref name="labels"/>
    /// to the <c>target</c> property. Returns the original schema unchanged if its shape
    /// doesn't match the expected <c>{ properties: { target: {...} } }</c> layout.
    /// </summary>
    private static JsonElement BuildEnumConstrainedSchema(JsonElement baseSchema, IReadOnlyCollection<string> labels)
    {
        if (JsonNode.Parse(baseSchema.GetRawText()) is JsonObject root
            && root["properties"] is JsonObject properties
            && properties["target"] is JsonObject target)
        {
            var enumValues = new JsonArray();
            foreach (var label in labels)
            {
                enumValues.Add(label);
            }
            target["enum"] = enumValues;
            return JsonSerializer.SerializeToElement(root);
        }

        return baseSchema;
    }

    private static string BuildDescription(CompiledStage stage)
    {
        var sb = new StringBuilder();
        sb.Append("Advance the call workflow out of stage '").Append(stage.Id).Append("'. ");
        sb.AppendLine("Pick the matching `target` when its condition is met:");
        foreach (var edge in stage.OutgoingEdges)
        {
            sb.Append("- `").Append(edge.Label).Append("` → stage `").Append(edge.TargetStageId).Append("`");
            if (!string.IsNullOrWhiteSpace(edge.Blueprint.When))
            {
                sb.Append(" — ").Append(edge.Blueprint.When);
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Wraps the factory-built advance function to expose an <c>enum</c>-constrained
/// <see cref="AIFunction.JsonSchema"/> for the <c>target</c> parameter while forwarding
/// invocation to the inner function. Schema overriding is done here (rather than via
/// <see cref="AIFunctionFactoryOptions"/>) so it's independent of the factory's schema hooks.
/// </summary>
internal sealed class EnumConstrainedFunction(AIFunction inner, JsonElement jsonSchema)
    : DelegatingAIFunction(inner)
{
    public override JsonElement JsonSchema { get; } = jsonSchema;
}

/// <summary>Structured envelope returned by the advance function so the model can react deterministically.</summary>
public sealed record AdvanceFunctionResult
{
    /// <summary>True when the workflow advanced to a new stage.</summary>
    public required bool Advanced { get; init; }

    /// <summary>The stage id the workflow landed on. Null when <see cref="Advanced"/> is false.</summary>
    public string? Stage { get; init; }

    /// <summary>Explanation when the advance was denied.</summary>
    public string? Note { get; init; }

    internal static AdvanceFunctionResult Ok(string stage) =>
        new() { Advanced = true, Stage = stage };

    internal static AdvanceFunctionResult Denied(string reason) =>
        new() { Advanced = false, Note = reason };
}
