using Agents.AI.ContactCenter.Authentication.Authenticators;
using Azure.Communication.Sms;
using ContactCenter.AIAgent.Configuration;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Services;

public sealed class AcsSmsOtpSender(SmsClient client, IOptions<BankingDemoOptions> options) : ISmsOtpSender
{
    public async Task SendAsync(string phoneNumberE164, string code, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Customers.Any(c => c.PhoneNumber == phoneNumberE164))
        {
            throw new InvalidOperationException("OTP delivery is restricted to configured customer phones on file.");
        }
        var result = await client.SendAsync(options.Value.SmsFrom, phoneNumberE164,
            $"Your demo contact-center verification code is {code}. It expires in five minutes. Enter it using your phone keypad; do not share it.",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Value.Successful)
        {
            throw new InvalidOperationException($"SMS delivery request failed with status {result.Value.HttpStatusCode}.");
        }
    }
}
