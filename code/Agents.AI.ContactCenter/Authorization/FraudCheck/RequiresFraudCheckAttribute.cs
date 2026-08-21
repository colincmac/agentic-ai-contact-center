using Agents.AI.Extensions.ToolApproval;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

// Attribute to mark tools as requiring fraud check
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresFraudCheckAttribute : Attribute, IToolApprovalRequirementData
{
    public double MaxRiskScore { get; }

    public RequiresFraudCheckAttribute(double maxRiskScore = 50.0)
    {
        MaxRiskScore = maxRiskScore;
    }

    public IEnumerable<IToolApprovalRequirement> GetRequirements()
    {
        yield return new RequiresFraudCheckRequirement(MaxRiskScore);
    }
}
