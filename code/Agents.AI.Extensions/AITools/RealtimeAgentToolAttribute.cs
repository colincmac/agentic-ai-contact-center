using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Microsoft.Graph.Models.TermStore;

namespace Agents.AI.Extensions.AITools;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RealtimeAgentToolAttribute : DescriptionAttribute
{
    public const string DefaultToolCallPreambleHeader = "Preamble sample phrases:";
    public string ToolCallPreambleHeader { get; }

    public string? ToolCallPreambleSamplePhrases { get; }

    public override string Description => !string.IsNullOrEmpty(ToolCallPreambleSamplePhrases) ? $"""
        {base.Description}{Environment.NewLine}
        {ToolCallPreambleHeader}{Environment.NewLine}
        {ToolCallPreambleSamplePhrases}{Environment.NewLine}
        """ : base.Description;

    /// <summary>
    /// Initializes a new instance of the RealtimeAgentToolAttribute class with the specified description, preamble
    /// header, and sample phrases for tool calls.
    /// </summary>
    /// <param name="description">A brief description of the tool or capability represented by this attribute. Cannot be null.</param>
    /// <param name="toolCallPreambleHeader">The header text to display before tool call preamble content. If null, a default header is used.</param>
    /// <param name="toolCallPreambleSamplePhrases">Sample phrases to use before calling the tool.</param>
    /// <remarks>
    /// If <paramref name="toolCallPreambleSamplePhrases"/> is null or empty, the <see cref="Description"/> property will not include the preamble section.
    /// <br />
    /// Example: <br />
    /// <br />
    /// Preamble sample phrases: <br />
    /// - For security, I’ll pull up your account using the email on file.<br />
    /// - Let me look up your account by {email} now.<br />
    /// - I’m fetching the account linked to {phone} to verify access.<br />
    /// - One moment - I’m opening your account details.<br />
    /// </remarks>
    public RealtimeAgentToolAttribute(string description, string? toolCallPreambleHeader, string? toolCallPreambleSamplePhrases) : base(description)
    {
         ToolCallPreambleHeader = toolCallPreambleHeader ?? DefaultToolCallPreambleHeader;
         ToolCallPreambleSamplePhrases = toolCallPreambleSamplePhrases;
    }
}
