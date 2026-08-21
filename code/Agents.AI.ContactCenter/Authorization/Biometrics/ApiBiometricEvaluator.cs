using System.Diagnostics;
using Agents.AI.RealtimeVoice.Azure.Biometrics.Grpc;
using Agents.AI.ContactCenter.Configuration;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Authorization.Biometrics;

/// <summary>
/// Evaluates voice biometric characteristics using the external biometrics gRPC API service.
/// </summary>
public sealed class ApiBiometricEvaluator : IVoiceBiometricEvaluator
{
    private readonly ILogger<ApiBiometricEvaluator> _logger;
    private readonly VoiceBiometricsApiOptions _options;
    private readonly BiometricService.BiometricServiceClient _client;

    /// <summary>
    /// Activity source for telemetry.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Agents.AI.Biometrics", "1.0.0");

    public ApiBiometricEvaluator(
        BiometricService.BiometricServiceClient client,
        IOptions<VoiceBiometricsApiOptions> options,
        ILogger<ApiBiometricEvaluator> logger)
    {
        _options = options.Value;
        _logger = logger;

        _client = client;

        _logger.LogInformation(
            "Initialized API biometric evaluator with endpoint {Endpoint}",
            _options.Endpoint);
    }

    /// <summary>
    /// Enrolls a participant's voice profile using the biometrics API.
    /// </summary>
    public async Task<VoiceEnrollmentResult> EnrollVoiceAsync(
        string participantId,
        ReadOnlyMemory<byte> audioSample,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("BiometricEnrollment");
        activity?.SetTag("participant.id", participantId);
        activity?.SetTag("audio.bytes", audioSample.Length);

        try
        {
            _logger.LogInformation(
                "Enrolling voice for participant {ParticipantId} with {AudioBytes} bytes",
                participantId, audioSample.Length);

            using var call = _client.EnrollUser(
                cancellationToken: cancellationToken,
                deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds));

            // First message: send user_id
            await call.RequestStream.WriteAsync(new EnrollRequest
            {
                UserId = participantId
            }, cancellationToken);

            // Second message: send audio chunk
            await call.RequestStream.WriteAsync(new EnrollRequest
            {
                AudioChunk = ByteString.CopyFrom(audioSample.Span)
            }, cancellationToken);

            // Complete the request stream
            await call.RequestStream.CompleteAsync();

            // Get response
            var response = await call.ResponseAsync;

            activity?.SetTag("enrollment.success", response.Success);
            activity?.SetTag("enrollment.message", response.Message);

            _logger.LogInformation(
                "Enrollment result for participant {ParticipantId}: Success={Success}, Message={Message}",
                participantId, response.Success, response.Message);

            return new VoiceEnrollmentResult
            {
                Success = response.Success,
                IsComplete = response.Success,
                SamplesCollected = response.Success ? 1 : 0,
                SamplesRequired = 1,
                ErrorMessage = response.Success ? null : response.Message
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogError(ex,
                "Biometrics service unavailable during enrollment for participant {ParticipantId}",
                participantId);

            activity?.SetStatus(ActivityStatusCode.Error, "Service unavailable");

            return new VoiceEnrollmentResult
            {
                Success = false,
                IsComplete = false,
                SamplesCollected = 0,
                SamplesRequired = 1,
                ErrorMessage = "Voice biometrics service is currently unavailable. Please try again later."
            };
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex,
                "gRPC error during enrollment for participant {ParticipantId}: {Status}",
                participantId, ex.StatusCode);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return new VoiceEnrollmentResult
            {
                Success = false,
                IsComplete = false,
                SamplesCollected = 0,
                SamplesRequired = 1,
                ErrorMessage = $"Enrollment failed: {ex.Status.Detail}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during enrollment for participant {ParticipantId}",
                participantId);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return new VoiceEnrollmentResult
            {
                Success = false,
                IsComplete = false,
                SamplesCollected = 0,
                SamplesRequired = 1,
                ErrorMessage = "An unexpected error occurred during voice enrollment."
            };
        }
    }

    /// <summary>
    /// Verifies if the voice sample matches the enrolled profile using the biometrics API.
    /// </summary>
    public async Task<VoiceVerificationResult> VerifyVoiceAsync(
        string participantId,
        ReadOnlyMemory<byte> audioSample,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("BiometricVerification");
        activity?.SetTag("participant.id", participantId);
        activity?.SetTag("audio.bytes", audioSample.Length);

        try
        {
            _logger.LogInformation(
                "Verifying voice for participant {ParticipantId} with {AudioBytes} bytes",
                participantId, audioSample.Length);

            using var call = _client.VerifyUser(
                cancellationToken: cancellationToken,
                deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds));

            // First message: send user_id
            await call.RequestStream.WriteAsync(new VerifyRequest
            {
                UserId = participantId
            }, cancellationToken);

            // Second message: send audio chunk
            await call.RequestStream.WriteAsync(new VerifyRequest
            {
                AudioChunk = ByteString.CopyFrom(audioSample.Span)
            }, cancellationToken);

            // Complete the request stream
            await call.RequestStream.CompleteAsync();

            // Get response
            var response = await call.ResponseAsync;

            var confidenceScore = response.SimilarityScore;
            var isMatch = response.IsMatch;

            activity?.SetTag("verification.match", isMatch);
            activity?.SetTag("verification.confidence", confidenceScore);

            _logger.LogInformation(
                "Verification result for participant {ParticipantId}: Match={IsMatch}, Confidence={Confidence:P2}",
                participantId, isMatch, confidenceScore);

            return new VoiceVerificationResult
            {
                Success = true,
                IsMatch = isMatch,
                ConfidenceScore = confidenceScore,
                VerifiedAt = DateTimeOffset.UtcNow
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            _logger.LogWarning(
                "No enrolled profile found for participant {ParticipantId}",
                participantId);

            activity?.SetStatus(ActivityStatusCode.Ok, "Not enrolled");

            return new VoiceVerificationResult
            {
                Success = false,
                IsMatch = false,
                ConfidenceScore = 0.0,
                ErrorMessage = "No voice profile found. Please complete enrollment first."
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogError(ex,
                "Biometrics service unavailable during verification for participant {ParticipantId}",
                participantId);

            activity?.SetStatus(ActivityStatusCode.Error, "Service unavailable");

            return new VoiceVerificationResult
            {
                Success = false,
                IsMatch = false,
                ConfidenceScore = 0.0,
                ErrorMessage = "Voice biometrics service is currently unavailable. Please try again later."
            };
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex,
                "gRPC error during verification for participant {ParticipantId}: {Status}",
                participantId, ex.StatusCode);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return new VoiceVerificationResult
            {
                Success = false,
                IsMatch = false,
                ConfidenceScore = 0.0,
                ErrorMessage = $"Verification failed: {ex.Status.Detail}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during verification for participant {ParticipantId}",
                participantId);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return new VoiceVerificationResult
            {
                Success = false,
                IsMatch = false,
                ConfidenceScore = 0.0,
                ErrorMessage = "An unexpected error occurred during voice verification."
            };
        }
    }

    /// <summary>
    /// Analyzes voice for anomalies. Not supported by the current API - returns default analysis.
    /// </summary>
    public Task<VoiceAnomalyAnalysis> AnalyzeVoiceAnomaliesAsync(
        string participantId,
        ReadOnlyMemory<byte> audioSample,
        CancellationToken cancellationToken = default)
    {
        // The current biometrics API doesn't support anomaly detection
        // Return a default analysis indicating no anomalies detected
        _logger.LogDebug(
            "Anomaly analysis requested for participant {ParticipantId} - returning default (not supported by API)",
            participantId);

        return Task.FromResult(new VoiceAnomalyAnalysis
        {
            ParticipantId = participantId,
            AnalyzedAt = DateTimeOffset.UtcNow,
            IsSyntheticVoiceDetected = false,
            StressLevel = StressLevel.Normal,
            BackgroundNoiseLevel = 0.0,
            AnomalyScore = 0.0
        });
    }

    /// <summary>
    /// Gets the voice profile for a participant. Not supported by the current API.
    /// </summary>
    public VoiceBiometricProfile? GetProfile(string participantId)
    {
        // The current API doesn't expose profile retrieval
        // This would require caching enrollment status locally or extending the API
        _logger.LogDebug(
            "Profile retrieval requested for participant {ParticipantId} - not supported by API",
            participantId);

        return null;
    }

    /// <summary>
    /// Deletes the voice profile for a participant. Not supported by the current API.
    /// </summary>
    public bool DeleteProfile(string participantId)
    {
        // The current API doesn't expose profile deletion
        _logger.LogWarning(
            "Profile deletion requested for participant {ParticipantId} - not supported by API",
            participantId);

        return false;
    }

}
