using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Authentication;
using ContactCenter.AIAgent.Configuration;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Services;

/// <summary>Demo-only account summary. No real financial institution is connected.</summary>
public sealed record DemoAccountResponse(decimal Balance, string Currency, string CardLastFour, bool CardActive);

public interface IDemoBankingService
{
    Task<DemoAccountResponse> GetAccountAsync(string customerId, CancellationToken ct);
    Task<DemoAccountResponse> ActivateCardAsync(string customerId, string idempotencyKey, CancellationToken ct);
}

public sealed class DemoBankingService(IOptions<BankingDemoOptions> options) : IDemoBankingService, ICallerDirectory
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(
        options.Value.Customers.Select(c => new KeyValuePair<string, Account>(c.Id, new(c))), StringComparer.Ordinal);

    public Task<DemoAccountResponse> GetAccountAsync(string customerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var account = Get(customerId);
        lock (account.Gate) { return Task.FromResult(account.Snapshot()); }
    }

    public Task<DemoAccountResponse> ActivateCardAsync(string customerId, string idempotencyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var account = Get(customerId);
        lock (account.Gate)
        {
            // Activation is intrinsically idempotent for this customer's one demo debit card.
            account.Active = true;
            return Task.FromResult(account.Snapshot());
        }
    }

    public Task<CallerIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = _accounts.Values.SingleOrDefault(a => a.Customer.PhoneNumber == phoneNumberE164);
        return Task.FromResult(account is null ? null : Identity(account.Customer));
    }
    public Task<CallerIdentity?> FindByUserIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_accounts.TryGetValue(userId, out var account) ? Identity(account.Customer) : null);
    }
    public Task<CallerIdentity?> FindByAccountLast4Async(string last4, string? displayNameHint = null, CancellationToken cancellationToken = default)
        => Task.FromResult<CallerIdentity?>(null);

    private Account Get(string customerId) => _accounts.TryGetValue(customerId, out var account)
        ? account : throw new KeyNotFoundException("Demo customer was not found.");
    private static CallerIdentity Identity(DemoCustomer c) => new(c.Id, c.DisplayName, c.PhoneNumber, null, null,
        CallerVerificationLevel.None, DateTimeOffset.MinValue, "demo-directory", new Dictionary<string, object?>());

    private sealed class Account(DemoCustomer customer)
    {
        public DemoCustomer Customer { get; } = customer;
        public Lock Gate { get; } = new();
        public bool Active { get; set; }
        public DemoAccountResponse Snapshot() => new(Customer.Balance, "USD", Customer.CardLastFour, Active);
    }
}
