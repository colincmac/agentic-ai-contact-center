using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

/// <summary>
/// AI tools for voice biometric operations.
/// These tools can be invoked by AI agents during voice conversations to handle
/// identity verification through voice biometrics.
/// </summary>
public sealed class VoiceBiometricTools : IAIToolCollection
{
    private readonly IVoiceBiometricEvaluator _biometricEvaluator;
    private readonly ILogger<VoiceBiometricTools> _logger;

    // Error message patterns for categorizing verification failures
    private static class ErrorPatterns
    {
        public const string NotFound = "not found";
        public const string NotEnrolled = "not enrolled";
        public const string Unavailable = "unavailable";
        public const string Profile = "profile";
    }

    public VoiceBiometricTools(
        IVoiceBiometricEvaluator biometricEvaluator,
        ILogger<VoiceBiometricTools>? logger = null)
    {
        _biometricEvaluator = biometricEvaluator;
        _logger = logger ?? NullLogger<VoiceBiometricTools>.Instance;
    }

    [Description("Checks if a participant has an enrolled voice profile and returns their enrollment status.")]
    public Task<VoiceEnrollmentStatusResult> CheckVoiceEnrollmentAsync(
        [Description("The unique identifier of the participant to check")] string participantId,
        CancellationToken cancellationToken = default)
    {
        var profile = _biometricEvaluator.GetProfile(participantId);

        _logger.LogInformation(
            "Checked enrollment status for participant {ParticipantId}: Enrolled={IsEnrolled}",
            participantId, profile?.IsEnrolled ?? false);

        return Task.FromResult(new VoiceEnrollmentStatusResult
        {
            IsEnrolled = profile?.IsEnrolled ?? false,
            EnrollmentSamples = profile?.EnrollmentSamples ?? 0,
            LastEnrolledAt = profile?.LastEnrolledAt,
            VerificationAttempts = profile?.VerificationAttempts ?? 0,
            SuccessfulVerifications = profile?.SuccessfulVerifications ?? 0
        });
    }

    [Description("Initiates voice enrollment by processing an audio sample from the participant. Returns enrollment progress.")]
    public async Task<VoiceEnrollmentResult> EnrollVoiceAsync(
        [Description("The unique identifier of the participant")] string participantId,
        [Description("The raw audio sample bytes (16kHz, 16-bit PCM mono)")] byte[] audioSample,
        CancellationToken cancellationToken = default)
    {
        if (audioSample is null || audioSample.Length == 0)
        {
            _logger.LogWarning("Enrollment attempted with empty audio sample for participant {ParticipantId}", participantId);

            return new VoiceEnrollmentResult
            {
                Success = false,
                IsComplete = false,
                ErrorMessage = "No audio sample provided. Please speak clearly so I can hear your voice."
            };
        }

        _logger.LogInformation(
            "Processing voice enrollment for participant {ParticipantId} with {AudioBytes} bytes",
            participantId, audioSample.Length);

        var result = await _biometricEvaluator.EnrollVoiceAsync(
            participantId,
            audioSample,
            cancellationToken);

        return result;
    }

    [Description("Verifies a participant's identity using their voice sample against their enrolled profile. Returns match status and confidence score.")]
    public async Task<VoiceVerificationToolResult> VerifyVoiceAsync(
        [Description("The unique identifier of the participant to verify")] string participantId,
        [Description("The raw audio sample bytes (16kHz, 16-bit PCM mono)")] byte[] audioSample,
        CancellationToken cancellationToken = default)
    {
        if (audioSample is null || audioSample.Length == 0)
        {
            _logger.LogWarning("Verification attempted with empty audio sample for participant {ParticipantId}", participantId);

            return new VoiceVerificationToolResult
            {
                Success = false,
                IsMatch = false,
                ConfidenceScore = 0,
                Message = "No audio sample provided. Please speak clearly so I can verify your voice."
            };
        }

        _logger.LogInformation(
            "Processing voice verification for participant {ParticipantId} with {AudioBytes} bytes",
            participantId, audioSample.Length);

        var result = await _biometricEvaluator.VerifyVoiceAsync(
            participantId,
            audioSample,
            cancellationToken);

        var message = result.Success switch
        {
            true when result.IsMatch => $"Voice verified successfully with {result.ConfidenceScore:P0} confidence.",
            true when !result.IsMatch => $"Voice verification did not match. Confidence was {result.ConfidenceScore:P0}.",
            false when IsNotEnrolledError(result.ErrorMessage) =>
                "You haven't enrolled your voice yet. Would you like to set up voice verification now?",
            false when IsUnavailableError(result.ErrorMessage) =>
                "Voice verification is temporarily unavailable. Please try again later or use an alternative verification method.",
            _ => result.ErrorMessage ?? "Voice verification failed. Please try speaking more clearly."
        };

        return new VoiceVerificationToolResult
        {
            Success = result.Success,
            IsMatch = result.IsMatch,
            ConfidenceScore = result.ConfidenceScore,
            VerifiedAt = result.VerifiedAt,
            Message = message
        };
    }

    [Description("Prompts the participant to provide a voice sample for verification or enrollment.")]
    public Task<string> RequestVoiceSampleAsync(
        [Description("The unique identifier of the participant")] string participantId,
        [Description("The purpose of the voice sample: 'enrollment' or 'verification'")] string purpose,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Requesting voice sample for participant {ParticipantId} for {Purpose}",
            participantId, purpose);

        var prompt = purpose.ToLowerInvariant() switch
        {
            "enrollment" => "To set up voice verification, I need to learn your voice. Please say the following phrase clearly: " +
                           "\"My voice is my password. Verify me.\"",
            "verification" => "Please speak now so I can verify your identity. You can say your name or any phrase.",
            _ => "Please speak clearly so I can process your voice."
        };

        return Task.FromResult(prompt);
    }

    [Description("Checks if voice biometric verification is available and operational.")]
    public Task<BiometricsAvailabilityResult> CheckBiometricsAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        // We can't easily check availability without making a call
        // Return a status based on whether we have a valid evaluator
        var isAvailable = _biometricEvaluator is not null;

        return Task.FromResult(new BiometricsAvailabilityResult
        {
            IsAvailable = isAvailable,
            Message = isAvailable
                ? "Voice biometric verification is available."
                : "Voice biometric verification is not configured."
        });
    }

    private static bool IsNotEnrolledError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains(ErrorPatterns.NotFound, StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains(ErrorPatterns.NotEnrolled, StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains(ErrorPatterns.Profile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnavailableError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains(ErrorPatterns.Unavailable, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<AITool> AsAITools()
    {
        yield return AIFunctionFactory.Create(CheckVoiceEnrollmentAsync);
        yield return AIFunctionFactory.Create(EnrollVoiceAsync);
        yield return AIFunctionFactory.Create(VerifyVoiceAsync);
    }
}

/// <summary>
/// Result of checking voice enrollment status.
/// </summary>
public sealed class VoiceEnrollmentStatusResult
{
    public bool IsEnrolled { get; set; }
    public int EnrollmentSamples { get; set; }
    public DateTimeOffset? LastEnrolledAt { get; set; }
    public int VerificationAttempts { get; set; }
    public int SuccessfulVerifications { get; set; }
}

/// <summary>
/// Result of voice verification tool invocation.
/// </summary>
public sealed class VoiceVerificationToolResult
{
    public bool Success { get; set; }
    public bool IsMatch { get; set; }
    public double ConfidenceScore { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Result of checking biometrics availability.
/// </summary>
public sealed class BiometricsAvailabilityResult
{
    public bool IsAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
}
