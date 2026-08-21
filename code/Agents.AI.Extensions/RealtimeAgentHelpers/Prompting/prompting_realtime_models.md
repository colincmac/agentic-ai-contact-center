
# Realtime AI Prompt Template

A fluent API for building structured system prompts for OpenAI Realtime voice agents. This library implements best practices from the [OpenAI Realtime Prompting Guide](https://cookbook.openai.com/examples/realtime_prompting_guide) to help you create effective, maintainable voice agent prompts.

## Quick Start

```
using Agents.AI.Extensions;

var prompt = RealtimePrompt.CreateBuilder()
    .WithRole(
        identity: "You are a friendly customer service agent for Contoso Electronics",
        objective: "Help customers resolve product issues and answer questions")
    .WithPersonality(p => p
        .Personality("Friendly, calm, and approachable")
        .Tone("Warm, concise, confident")
        .Length("2-3 sentences per turn")
        .EnforceVariety())
    .WithSafety(s => s
        .UseDefaultEscalationConditions()
        .MaxFailedToolAttempts(2))
    .BuildAndRender();
```

## Prompt Structure

The template organizes prompts into clearly labeled sections that the model can easily parse and follow:

| Section | Purpose |
|---------|---------|
| **Role & Objective** | Who the agent is and what success looks like |
| **Personality & Tone** | Voice style, pacing, and language constraints |
| **Context** | Retrieved context or relevant information |
| **Reference Pronunciations** | Phonetic guides for tricky words |
| **Tools** | Tool names, usage rules, and preambles |
| **Instructions/Rules** | Do's, don'ts, and approach |
| **Conversation Flow** | States, goals, and transitions |
| **Sample Phrases** | Example phrases for variety |
| **Safety & Escalation** | Fallback and handoff logic |

## General Prompting Tips

### Do's

- ✅ **Iterate relentlessly** — Small wording changes can significantly impact behavior
- ✅ **Prefer bullets over paragraphs** — Clear, short bullets outperform long paragraphs
- ✅ **Guide with examples** — The model closely follows sample phrases
- ✅ **Be precise** — Ambiguity or conflicting instructions degrade performance
- ✅ **Use capitalized text for emphasis** — Key rules stand out better
- ✅ **Convert logic to natural language** — Write "IF MORE THAN THREE FAILURES THEN ESCALATE" instead of `IF x > 3 THEN ESCALATE`

### Don'ts

- ❌ Don't use vague or conflicting instructions
- ❌ Don't write long paragraphs — use bullet points
- ❌ Don't assume the model will infer unstated rules
- ❌ Don't mention tools in prompts that aren't in the tools list

---

## API Reference

### Role & Objective

Defines who the agent is and what "done" means.

```
.WithRole(
    identity: "You are a French Quebecois speaking customer service bot",
    objective: "Answer customer questions about their account",
    characterTraits: "Speak with a warm Quebec accent")
```

**When to use:** When the model isn't taking on the persona, role, or task scope you need.

---

### Personality & Tone

Configures voice style, brevity, and pacing for natural, consistent responses.

```
.WithPersonality(p => p
    .Personality("Friendly, calm, and approachable expert")
    .Tone("Warm, concise, confident, never fawning")
    .Length("2-3 sentences per turn")
    .Pacing("Deliver your audio response fast, but do not sound rushed")
    .Enthusiasm("Calm and measured")
    .Formality("Professional but approachable")
    .Emotion("Compassionate when addressing problems")
    .FillerWords("occasionally")
    .EnforceVariety())
```

#### Language Constraints

Pin output to a target language to prevent unwanted language switching:

```
.WithPersonality(p => p
    .Personality("Helpful assistant")
    .Tone("Professional")
    .PinToLanguage("English", 
        nonPrimaryResponse: "If the user speaks another language, politely explain that support is limited to English."))
```

For language tutoring or code-switching scenarios:

```
.WithPersonality(p => p
    .Personality("Friendly French tutor")
    .Tone("Encouraging and patient")
    .WithLanguage(
        primaryLanguage: "French",
        allowOthers: true,
        codeSwitchingRules: "Use English for grammar explanations, French for practice conversations"))
```

#### Reduce Repetition

Enable variety enforcement to prevent robotic, repetitive responses:

```
.WithPersonality(p => p
    .Personality("Customer service agent")
    .Tone("Helpful")
    .EnforceVariety()) // Adds: "Do not repeat the same sentence twice"
```

---

### Reference Pronunciations

Provides phonetic guides for brand names, technical terms, or locations.

```
.AddPronunciation("SQL", "sequel")
.AddPronunciation("PostgreSQL", "post-gress")
.AddPronunciation("Kyiv", "KEE-iv")
.AddPronunciations(
    ("Azure", "AZH-ur"),
    ("Huawei", "HWAH-way"))
```

**When to use:** When brand names, technical terms, or locations are mispronounced.

---

### Instructions & Rules

Configures general rules, unclear audio handling, and sound suppression.

```
.WithInstructions(i => i
    .AddRules(
        "Follow the Conversation States closely",
        "If a user provides a name or phone number, always repeat it back to confirm",
        "If the caller corrects any detail, acknowledge and confirm the new value")
    .EnableCharacterByCharacterPronunciation()
    .HandleUnclearAudio(
        askForClarification: true,
        clarificationPhrases: ["I didn't catch that. Could you repeat?", "Sorry, I missed that."])
    .SuppressBackgroundSounds())
```

#### Alphanumeric Pronunciations

For phone numbers, credit cards, order IDs, etc.:

```
.WithInstructions(i => i
    .EnableCharacterByCharacterPronunciation())
    // Adds: "When reading numbers or codes, speak each character separately, 
    //        separated by hyphens (e.g., 4-1-5)"
```

#### Unclear Audio Handling

```
.WithInstructions(i => i
    .HandleUnclearAudio(
        askForClarification: true,
        repeatLastQuestion: false,
        clarificationPhrases: [
            "I didn't quite catch that. Could you say that again?",
            "Sorry, could you repeat that?"
        ]))
```

---

### Tool Configuration

Controls how the model uses function calls.

#### Tool Behaviors

| Behavior | Description |
|----------|-------------|
| `Proactive` | Call immediately without confirmation or preamble |
| `ConfirmationFirst` | Ask for user confirmation before calling |
| `Preambles` | Output a preamble phrase while calling |

```
.WithTools(t => t
    .GlobalPreamble("Before any tool call, say one short line like 'I'm checking that now.'")
    
    // Proactive tool - calls immediately
    .AddProactiveTool(
        name: "lookup_account",
        useWhen: "verifying identity or viewing plan/outage flags",
        doNotUseWhen: "the user is anonymous and only asks general questions")
    
    // Confirmation required before calling
    .AddConfirmationTool(
        name: "refund_credit",
        useWhen: "confirmed outage > 240 minutes in the past 7 days",
        confirmationPhrase: "I can issue a credit for this outage—would you like me to go ahead?",
        doNotUseWhen: "outage is unconfirmed")
    
    // Speaks preamble while calling
    .AddPreambleTool(
        name: "check_outage",
        useWhen: "user reports connectivity issues or slow speeds",
        preamblePhrases: [
            "I'll check for any outages at your address right now.",
            "Let me look up network status for your area."
        ]))
```

#### Supervisor Tool (Responder-Thinker Architecture)

For architectures where a realtime model (responder) works with a text model (thinker):

```
.WithTools(t => t
    .WithSupervisorTool(
        callWhen: [
            "Any request outside the allow list",
            "Any factual, policy, account, or process question"
        ],
        doNotCallWhen: [
            "Simple greetings and basic chitchat",
            "Requests to repeat or clarify"
        ],
        approvedFillers: [
            "One moment.",
            "Let me check.",
            "Just a second."
        ],
        rephraseInstructions: """
            - Start with a brief conversational opener using active language
            - Keep it short: no more than 2 sentences
            - Read numbers for speech: money naturally, phone numbers 3-3-4
            """))
```

---

### Conversation Flow

Structures dialogue into clear, goal-driven phases using a state machine pattern.

```
.AddConversationState(s => s
    .Id("1_greeting")
    .Goal("Set tone and invite the reason for calling")
    .Description("Greet the caller and identify the service")
    .AddInstructions(
        "Identify as NorthLoop Internet Support",
        "Keep the opener brief and invite the caller's goal")
    .AddExamples(
        "Thanks for calling NorthLoop Internet—how can I help today?",
        "You've reached NorthLoop Support. What's going on with your service?")
    .ExitWhen("Caller states an initial goal or symptom")
    .TransitionTo("2_discover", "After greeting is complete"))

.AddConversationState(s => s
    .Id("2_discover")
    .Goal("Classify the issue and capture minimal details")
    .Description("Determine billing vs connectivity with targeted questions")
    .AddInstructions(
        "Determine billing vs connectivity with one targeted question",
        "For connectivity: collect the service address",
        "For billing/account: collect email or phone used on the account")
    .AddExamples(
        "Is this about your bill or your internet speed?",
        "What address are you using for the connection?")
    .ExitWhen("Intent and address (for connectivity) or email/phone (for billing) are known")
    .TransitionTo("3_verify", "Once intent is determined"))

.AddConversationState(s => s
    .Id("3_verify")
    .Goal("Confirm identity and retrieve the account")
    .Description("Use lookup_account to verify the caller")
    .AddInstructions(
        "Once you have email or phone, call lookup_account(email_or_phone)",
        "If lookup fails, try the alternate identifier once")
    .ExitWhen("Account ID is returned")
    .TransitionTo("4_resolve", "Once verified"))
```

#### JSON Export for Dynamic Flows

For dynamic conversation flow updates via `session.update`:

```
var states = new List<ConversationState> { /* ... */ };
var json = RealtimeAIPromptTemplate.RenderConversationFlowAsJson(states);
```

---

### Sample Phrases

Provides anchor examples for consistent style without rigid responses.

```
.WithSamplePhrases(p => p
    .Acknowledgements("On it.", "One moment.", "Good question.")
    .Clarifiers("Do you want A or B?", "What's the deadline?")
    .Bridges("Here's the quick plan.", "Let's keep it simple.")
    .Empathy("That's frustrating—let's fix it.")
    .Closers("Anything else before we wrap?", "Happy to help next time."))

// Or use defaults
.WithSamplePhrases(p => p.UseDefaults())
```

---

### Safety & Escalation

Defines when and how to escalate to a human agent.

```
.WithSafety(s => s
    .UseDefaultEscalationConditions() // Adds common safety triggers
    .EscalateWhen("Billing disputes over $50")
    .MaxFailedToolAttempts(2)
    .MaxNoMatchEvents(3)
    .EscalationPhrases(
        "Thanks for your patience—I'm connecting you with a specialist now.")
    .EscalationExamples(
        "This is the third time the reset didn't work. Just get me a person.",
        "I am extremely frustrated!"))
```

**Default escalation conditions include:**
- Safety risk (self-harm, threats, harassment)
- User explicitly asks for a human
- Severe dissatisfaction
- Out-of-scope or restricted topics

---

## Complete Example

Here's a full example for a customer service voice agent:

```
var prompt = RealtimePrompt.CreateBuilder()
    .WithRole(
        identity: "You are a friendly customer service agent for NorthLoop Internet",
        objective: "Help customers resolve connectivity issues and manage their accounts")
    
    .WithPersonality(p => p
        .Personality("Friendly, calm, and approachable expert")
        .Tone("Warm, concise, confident, never fawning")
        .Length("2-3 sentences per turn")
        .Pacing("Speak at a natural pace, not rushed")
        .PinToLanguage("English")
        .EnforceVariety())
    
    .WithContext("Customer accounts are verified using phone number or email.")
    
    .AddPronunciations(
        ("SQL", "sequel"),
        ("NorthLoop", "north-loop"))
    
    .WithInstructions(i => i
        .AddRules(
            "Follow the Conversation States closely",
            "Always repeat back phone numbers and emails to confirm")
        .EnableCharacterByCharacterPronunciation()
        .HandleUnclearAudio(askForClarification: true)
        .SuppressBackgroundSounds())
    
    .WithTools(t => t
        .AddProactiveTool("lookup_account", 
            useWhen: "verifying identity",
            doNotUseWhen: "user is anonymous")
        .AddPreambleTool("check_outage",
            useWhen: "user reports connectivity issues",
            preamblePhrases: ["Let me check for outages in your area."]))
    
    .AddConversationState(s => s
        .Id("1_greeting")
        .Goal("Set tone and invite the reason for calling")
        .Description("Greet the caller warmly")
        .AddInstructions("Identify as NorthLoop Internet Support")
        .AddExamples("Thanks for calling NorthLoop—how can I help?")
        .TransitionTo("2_discover", "After greeting"))
    
    .AddConversationState(s => s
        .Id("2_discover")
        .Goal("Classify the issue")
        .Description("Determine billing vs connectivity")
        .AddInstructions("Ask one targeted question to classify")
        .TransitionTo("3_verify", "Once classified"))
    
    .WithSamplePhrases(p => p.UseDefaults())
    
    .WithSafety(s => s
        .UseDefaultEscalationConditions()
        .MaxFailedToolAttempts(2)
        .EscalationPhrases("Let me connect you with a specialist."))
    
    .BuildAndRender();
```

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Model doesn't follow persona | Add more specific `Role & Objective` details |
| Responses are too long | Set explicit `Length` in personality |
| Repetitive responses | Enable `EnforceVariety()` |
| Wrong language switching | Use `PinToLanguage()` |
| Mispronounced words | Add `ReferencePronunciation` entries |
| Tool called at wrong time | Add explicit `UseWhen`/`DoNotUseWhen` rules |
| Unclear audio issues | Configure `HandleUnclearAudio()` |
| Missing escalation | Add `SafetyAndEscalation` conditions |

---

## Integration with IVR Workflows

The prompt template integrates with the `IvrWorkflow` system:

```
// In your workflow activator
public async Task<IvrWorkflowDefinition> CreateWorkflowAsync(CallContext context)
{
    var prompt = RealtimePrompt.CreateBuilder()
        .WithRole("Customer service agent", "Verify caller identity")
        .WithPersonality(p => p
            .Personality("Professional and efficient")
            .Tone("Clear and helpful"))
        .BuildAndRender();

    return IvrWorkflowBuilder.Create("CallerVerification")
        .WithSystemPrompt(prompt)
        .AddInputStep(/* ... */)
        .Build();
}
```

---

## Resources

- [OpenAI Realtime Prompting Guide](https://cookbook.openai.com/examples/realtime_prompting_guide)
- [OpenAI Realtime API Documentation](https://platform.openai.com/docs/guides/realtime)
- [IVR Workflow README](../../LiveVoice/IvrWorkflow/README.md)
```
