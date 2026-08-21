using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Extensions.AI.RealtimeVoice.Configuration;

[JsonConverter(typeof(ConversationModalityJsonConverter))]
public sealed record ConversationModality
{
    private static readonly Dictionary<string, ConversationModality> knownModalities = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ReaderWriterLockSlim knownDictionaryLock = new();
    public static readonly ConversationModality Text = Register("text");
    public static readonly ConversationModality Audio = Register("audio");
    public static readonly ConversationModality Image = Register("image");

    public string Value { get; }

    private ConversationModality(string value)
    {
        Value = value;
    }

    public static IEnumerable<ConversationModality> Known
    {
        get
        {
            knownDictionaryLock.EnterReadLock();
            try
            {
                return knownModalities.Values.ToArray();
            }
            finally
            {
                knownDictionaryLock.ExitReadLock();
            }
        }
    }

    public static ConversationModality Register(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Modality value cannot be null or whitespace.", nameof(value));
        }

        knownDictionaryLock.EnterUpgradeableReadLock();
        try
        {
            if (knownModalities.TryGetValue(value, out var existing))
            {
                return existing;
            }

            knownDictionaryLock.EnterWriteLock();
            try
            {
                var created = new ConversationModality(value);
                knownModalities[value] = created;
                return created;
            }
            finally
            {
                knownDictionaryLock.ExitWriteLock();
            }
        }
        finally
        {
            knownDictionaryLock.ExitUpgradeableReadLock();
        }
    }

    public static bool TryGet(string value, [MaybeNullWhen(false)] out ConversationModality modality)
    {
        modality = null!;
        if (value is null)
        {
            return false;
        }

        knownDictionaryLock.EnterReadLock();
        try
        {
            return knownModalities.TryGetValue(value, out modality);
        }
        finally
        {
            knownDictionaryLock.ExitReadLock();
        }
    }

    // Parse but allow dynamic extension (set allowUnknown=false to restrict)
    public static bool TryParse(string value, [MaybeNullWhen(false)] out ConversationModality modality, bool allowUnknown = true)
    {
        modality = null!;
        if (value is null)
        {
            return false;
        }
        if (TryGet(value, out modality))
        {
            return true;
        }
        if (!allowUnknown)
        {
            return false;
        }
        modality = Register(value);
        return true;
    }

    public static ConversationModality Parse(string value, bool allowUnknown = true)
    {
        if (TryParse(value, out var m, allowUnknown))
        {
            return m;
        }
        throw new FormatException($"Unknown modality '{value}'.");
    }

    public override string ToString() => Value;
}


[JsonConverter(typeof(ConversationModalitySetJsonConverter))]
public readonly record struct ConversationModalitySet : IEnumerable<ConversationModality>
{
    private readonly ImmutableHashSet<ConversationModality> _set = [];
    private ImmutableHashSet<ConversationModality> Set => _set ?? ImmutableHashSet<ConversationModality>.Empty;

    private ConversationModalitySet(ImmutableHashSet<ConversationModality> set)
    {
        _set = set;
    }

    public static ConversationModalitySet Empty { get; } =
        new(ImmutableHashSet<ConversationModality>.Empty);

    public static ConversationModalitySet Of(params ConversationModality[] modalities) =>
        new(modalities.ToImmutableHashSet());

    public static ConversationModalitySet From(IEnumerable<ConversationModality> modalities) =>
        new(modalities.ToImmutableHashSet());

    public bool Contains(ConversationModality modality) => Set.Contains(modality);

    public ConversationModalitySet Add(ConversationModality modality) =>
        new(Set.Add(modality));

    public ConversationModalitySet Union(ConversationModalitySet other) =>
        new(Set.Union(other.Set).ToImmutableHashSet());

    public IEnumerator<ConversationModality> GetEnumerator() =>
        (Set ?? ImmutableHashSet<ConversationModality>.Empty).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(",", Set.Select(m => m.Value));
    public bool IsEmpty => Set == null || Set.Count == 0;

    public static bool TryParseCsv(string csv, out ConversationModalitySet set, bool allowUnknown = true)
    {
        set = Empty;
        if (string.IsNullOrWhiteSpace(csv))
        {
            return true;
        }
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<ConversationModality>();
        foreach (var p in parts)
        {
            if (!ConversationModality.TryParse(p, out var m, allowUnknown))
            {
                set = Empty;
                return false;
            }
            list.Add(m);
        }
        set = From(list);
        return true;
    }

    public static ConversationModalitySet ParseCsv(string csv, bool allowUnknown = true)
    {
        if (TryParseCsv(csv, out var set, allowUnknown))
        {
            return set;
        }
        throw new FormatException($"Failed to parse modality set from '{csv}'.");
    }
}
internal sealed class ConversationModalityJsonConverter : JsonConverter<ConversationModality>
{
    public override ConversationModality Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) { var value = reader.GetString(); return ConversationModality.Parse(value!); }
    public override void Write(Utf8JsonWriter writer, ConversationModality value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
internal sealed class ConversationModalitySetJsonConverter : JsonConverter<ConversationModalitySet>
{
    public override ConversationModalitySet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) { return ConversationModalitySet.ParseCsv(reader.GetString()!); }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<ConversationModality>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var value = reader.GetString();
                list.Add(ConversationModality.Parse(value!));
            }
            return ConversationModalitySet.From(list);
        }

        throw new JsonException("Expected string or array for ConversationModalitySet.");
    }

    public override void Write(Utf8JsonWriter writer, ConversationModalitySet value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var m in value)
        {
            writer.WriteStringValue(m.Value);
        }
        writer.WriteEndArray();
    }
}
