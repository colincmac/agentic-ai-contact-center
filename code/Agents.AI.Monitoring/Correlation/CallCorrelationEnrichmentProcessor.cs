using System.Diagnostics;
using OpenTelemetry;

namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// OpenTelemetry span processor that stamps the ambient <see cref="CallCorrelationContext"/>
/// (from <see cref="ICallCorrelationAccessor"/>) onto every started activity. This surfaces the
/// canonical call identifiers as span attributes — and, via the Azure Monitor exporter, as
/// App Insights <c>customDimensions</c> — so a whole call can be reconstructed by e2e_call_id.
/// </summary>
internal sealed class CallCorrelationEnrichmentProcessor : BaseProcessor<Activity>
{
    private readonly ICallCorrelationAccessor _accessor;

    public CallCorrelationEnrichmentProcessor(ICallCorrelationAccessor accessor) => _accessor = accessor;

    public override void OnStart(Activity activity)
    {
        var context = _accessor.Current;
        if (context is null)
        {
            return;
        }

        foreach (var tag in context.ToTags())
        {
            if (activity.GetTagItem(tag.Key) is null)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
        }
    }
}
