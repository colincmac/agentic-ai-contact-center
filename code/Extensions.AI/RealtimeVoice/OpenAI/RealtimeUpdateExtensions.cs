//using System.Text;
//using System.Text.Json;
//using System.Text.Json.Nodes;
//using System.Text.Json.Serialization;
//using System.Text.Json.Serialization.Metadata;
//using Microsoft.Extensions.AI;
//using OpenAI.Realtime;


//namespace Extensions.AI.RealtimeVoice.OpenAI;

//public static class RealtimeUpdateExtensions
//{
  
//    public static FunctionCallContent? GetFunctionCallContent(this OutputStreamingFinishedUpdate update, JsonSerializerOptions? jsonSerializerOptions = null)
//    {
//        var jsonOptions = jsonSerializerOptions ?? AIJsonUtilities.DefaultOptions;

//        return FunctionCallContent.CreateFromParsedArguments(
//            update.FunctionCallArguments, update.FunctionCallId, update.FunctionName,
//                argumentParser: json => JsonSerializer.Deserialize(json,
//                (JsonTypeInfo<IDictionary<string, object>>)jsonOptions.GetTypeInfo(typeof(IDictionary<string, object>)))!);
//    }

//    public static FunctionCallContent? GetFunctionCallContent(this RealtimeItem item, JsonSerializerOptions? jsonSerializerOptions = null)
//    {
//        var jsonOptions = jsonSerializerOptions ?? AIJsonUtilities.DefaultOptions;

//        return FunctionCallContent.CreateFromParsedArguments(
//            item.FunctionArguments, item.FunctionCallId, item.FunctionName,
//                argumentParser: json => JsonSerializer.Deserialize(json,
//                (JsonTypeInfo<IDictionary<string, object>>)jsonOptions.GetTypeInfo(typeof(IDictionary<string, object>)))!);
//    }

//    public static UsageDetails ToUsageDetails(this ConversationTokenUsage tokenUsage)
//    {
//        var destination = new UsageDetails
//        {
//            InputTokenCount = tokenUsage.InputTokenCount,
//            OutputTokenCount = tokenUsage.OutputTokenCount,
//            TotalTokenCount = tokenUsage.TotalTokenCount,
//            AdditionalCounts = [],
//        };

//        var counts = destination.AdditionalCounts;

//        if (tokenUsage.InputTokenDetails is ConversationInputTokenUsageDetails inputDetails)
//        {
//            const string InputDetails = nameof(ConversationTokenUsage.InputTokenDetails);
//            counts.Add($"{InputDetails}.{nameof(ConversationInputTokenUsageDetails.AudioTokenCount)}", inputDetails.AudioTokenCount);
//            counts.Add($"{InputDetails}.{nameof(ConversationInputTokenUsageDetails.CachedTokenCount)}", inputDetails.CachedTokenCount);
//            counts.Add($"{InputDetails}.{nameof(ConversationInputTokenUsageDetails.TextTokenCount)}", inputDetails.TextTokenCount);

//        }

//        if (tokenUsage.OutputTokenDetails is ConversationOutputTokenUsageDetails outputDetails)
//        {
//            const string OutputDetails = nameof(ConversationTokenUsage.OutputTokenDetails);
//            counts.Add($"{OutputDetails}.{nameof(ConversationOutputTokenUsageDetails.AudioTokenCount)}", outputDetails.AudioTokenCount);
//            counts.Add($"{OutputDetails}.{nameof(ConversationOutputTokenUsageDetails.TextTokenCount)}", outputDetails.TextTokenCount);

//        }

//        return destination;
//    }
  
//}

