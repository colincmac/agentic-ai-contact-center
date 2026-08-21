using Agents.AI.Extensions.ToolApproval;

namespace Agents.AI.Extensions.Exceptions;

public class AIToolExecutionException : Exception
{
    public AIToolExecutionException(string message) : base(message)
    {
    }

    public AIToolExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public class AIToolExecutionUnauthorizedException : AIToolExecutionException
{
    public ToolApprovalFailure? FailureDetails { get; }
    public AIToolExecutionUnauthorizedException(ToolApprovalFailure? failureDetails, string message) : base(message)
    {
        FailureDetails = failureDetails;
    }

    public AIToolExecutionUnauthorizedException(ToolApprovalFailure? failureDetails, string message, Exception innerException) : base(message, innerException)
    {
        FailureDetails = failureDetails;
    }
}
