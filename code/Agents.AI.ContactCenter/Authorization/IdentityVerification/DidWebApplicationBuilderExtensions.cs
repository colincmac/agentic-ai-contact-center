using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

public static class DidWebApplicationBuilderExtensions
{

    public static IHostApplicationBuilder AddIdentityVerification(this IHostApplicationBuilder builder, Action<DidOptions>? configure = null, string configurationSectionName = DidOptions.ConfigurationSectionName)
    {
        builder.AddDecentralizedIDOptions(configure, configurationSectionName);
        var section = builder.Configuration.GetSection(configurationSectionName);
        builder.Services.Configure<DidOptions>(section);

        // Manually build raw JsonElements. The configuration binder leaves JsonElement as Undefined.
        builder.Services.AddOptions<DidRawOptions>().Configure<IConfiguration>((opts, cfg) =>
        {
            var root = cfg.GetSection(configurationSectionName);
            opts.DidDocument = BuildJsonElement(root.GetSection("DidDocument"));
            opts.DidConfiguration = BuildJsonElement(root.GetSection("DidConfiguration"));
        });

        if (configure is not null)
        {
            builder.Services.PostConfigure(configure);
        }
        return builder;
    }

    public static IHostApplicationBuilder AddDecentralizedIDOptions(this IHostApplicationBuilder builder, Action<DidOptions>? configure = null, string configurationSectionName = DidOptions.ConfigurationSectionName)
    {
        var section = builder.Configuration.GetSection(configurationSectionName);
        builder.Services.Configure<DidOptions>(section);

        // Manually build raw JsonElements. The configuration binder leaves JsonElement as Undefined.
        builder.Services.AddOptions<DidRawOptions>().Configure<IConfiguration>((opts, cfg) =>
        {
            var root = cfg.GetSection(configurationSectionName);
            opts.DidDocument = BuildJsonElement(root.GetSection("DidDocument"));
            opts.DidConfiguration = BuildJsonElement(root.GetSection("DidConfiguration"));
        });

        if (configure is not null)
        {
            builder.Services.PostConfigure(configure);
        }
        return builder;
    }

    private static JsonElement BuildJsonElement(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return default; // JsonElement with ValueKind Undefined
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteSection(writer, section);
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static void WriteSection(Utf8JsonWriter writer, IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (IsArray(children))
        {
            writer.WriteStartArray();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
            {
                WriteSectionValue(writer, child);
            }
            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        foreach (var child in children)
        {
            writer.WritePropertyName(child.Key);
            WriteSectionValue(writer, child);
        }
        writer.WriteEndObject();
    }

    private static void WriteSectionValue(Utf8JsonWriter writer, IConfigurationSection section)
    {
        var grandchildren = section.GetChildren().ToList();
        if (grandchildren.Count == 0)
        {
            if (section.Value is null)
            {
                writer.WriteNullValue();
                return;
            }
            if (bool.TryParse(section.Value, out var b))
            {
                writer.WriteBooleanValue(b);
            }
            else if (long.TryParse(section.Value, out var l))
            {
                writer.WriteNumberValue(l);
            }
            else if (double.TryParse(section.Value, out var d))
            {
                writer.WriteNumberValue(d);
            }
            else
            {
                writer.WriteStringValue(section.Value);
            }
        }
        else
        {
            WriteSection(writer, section);
        }
    }

    private static bool IsArray(IEnumerable<IConfigurationSection> children)
    {
        var list = children.ToList();
        if (list.Count == 0)
        {
            return false;
        }
        return list.All(c => int.TryParse(c.Key, out _)) && list.Select(c => int.Parse(c.Key)).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, list.Count));
    }
}
