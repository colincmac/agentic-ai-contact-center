using System.Diagnostics;

namespace Agents.AI.Sandbox;

public sealed class SandboxTelemetry
{
    private readonly ActivitySource _activitySource = new (SandboxActivitySource.ActivitySourceName);

    public ActivitySource ActivitySource => _activitySource;

    public Activity? StartChildActivity(string name, string callId)
    {
        if (!_activitySource.HasListeners())
        {
            return null;
        }

        var activity = _activitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag(SandboxActivitySource.CallIdTag, callId);
        return activity;
    }

    /// <summary>
    /// Marks <paramref name="activity"/> as failed and attaches <c>error.type</c>
    /// / <c>error.message</c> tags so collectors can fire alerts on them.
    /// </summary>
    public static void SetError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("error.type", ex.GetType().FullName);
        activity.SetTag("error.message", ex.Message);
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
    }
}
