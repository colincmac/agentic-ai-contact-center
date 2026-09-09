using System.Text;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>Call-local deterministic capture. Raw credentials never become conversation events.</summary>
public sealed class CredentialCapture
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _digits = new();
    private IReadOnlyList<CredentialRequest> _requests = [];
    private CredentialRequest? _selected;
    private DateTimeOffset? _lastCompletedAt;

    public bool IsActive { get { lock (_gate) { return _requests.Count != 0; } } }

    public string Begin(AuthStepRender render)
    {
        lock (_gate)
        {
            _requests = render.Requests;
            _digits.Clear();
            if (_requests.Count == 0 || _requests.Count > 9
                || _requests.Any(r => r.Kind == CredentialKind.SpokenText))
            {
                throw new CredentialCaptureUnavailableException("This capture adapter requires a numeric credential.");
            }
            _selected = _requests.Count == 1 ? _requests[0] : null;
            return _selected is not null
                ? _selected.SsmlPrompt ?? $"Enter {_selected.Purpose}, followed by pound."
                : string.Join(" ", _requests.Select((r, i) => $"Press {i + 1} to verify using {r.AuthenticatorName}."));
        }
    }

    public void End()
    {
        lock (_gate)
        {
            if (_requests.Count != 0) { _lastCompletedAt = DateTimeOffset.UtcNow; }
            _requests = [];
            _selected = null;
            _digits.Clear();
        }
    }

    public bool SuppressesAudio(DateTimeOffset receivedAt)
    {
        lock (_gate) { return _requests.Count != 0 || (_lastCompletedAt is { } at && receivedAt <= at); }
    }

    public CredentialCaptureResult Accept(char digit)
    {
        lock (_gate)
        {
            if (_requests.Count == 0) { throw new InvalidOperationException("No credential capture is active."); }
            if (_selected is null)
            {
                var index = digit - '1';
                if (index < 0 || index >= _requests.Count)
                {
                    return new(null, null, "Please select one of the verification methods.");
                }
                _selected = _requests[index];
                return new(null, null, _selected.SsmlPrompt ?? $"Enter {_selected.Purpose}, followed by pound.");
            }
            if (digit == '*') { _digits.Clear(); return new(null, null, null); }
            if (digit is >= '0' and <= '9') { _digits.Append(digit); }
            if (digit != '#' && _digits.Length < (_selected.MaxLength ?? 32)) { return new(null, null, null); }
            if (_digits.Length < (_selected.MinLength ?? 1))
            {
                _digits.Clear();
                return new(null, null, "The value was too short. Please enter it again.");
            }
            var result = new CredentialCaptureResult(_selected.AuthenticatorName, new CredentialInput(_digits.ToString()), null);
            _digits.Clear();
            return result;
        }
    }
}

public sealed record CredentialCaptureResult(string? AuthenticatorName, CredentialInput? Input, string? Prompt);

public sealed class CredentialCaptureUnavailableException(string message) : Exception(message);
