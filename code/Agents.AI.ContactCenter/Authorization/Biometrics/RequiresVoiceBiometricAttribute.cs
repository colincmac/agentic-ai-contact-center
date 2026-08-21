using Agents.AI.Extensions.ToolApproval;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

// Attribute to mark tools as requiring voice biometric verification
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresVoiceBiometricAttribute : Attribute, IToolApprovalRequirementData
{
    public double ConfidenceThreshold { get; }
    public RequiresVoiceBiometricAttribute(double confidenceThreshold = 0.85)
    {
        ConfidenceThreshold = confidenceThreshold;
    }

    public IEnumerable<IToolApprovalRequirement> GetRequirements()
    {
        yield return new RequiresVoiceBiometricRequirement(ConfidenceThreshold);
    }
}
