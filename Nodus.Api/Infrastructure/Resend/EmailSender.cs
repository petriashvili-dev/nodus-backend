using Microsoft.Extensions.Options;
using Resend;

namespace Nodus.Api.Infrastructure.Resend;

public sealed record OutgoingEmail(string To, string Subject, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct);
}

public sealed class ResendOptions
{
    public const string Section = "Resend";

    public string ApiKey { get; init; } = "";
    public string From { get; init; } = "";
}

public sealed class ResendEmailSender(IResend resend, IOptions<ResendOptions> options) : IEmailSender
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        var message = new EmailMessage { From = options.Value.From, Subject = email.Subject, HtmlBody = email.HtmlBody };
        message.To.Add(email.To);
        var response = await resend.EmailSendAsync(message, ct);
        if (!response.Success)
            throw new InvalidOperationException(response.Exception?.Message ?? "Unable to send email through Resend.");
    }
}

/// <summary>Development fallback when no Resend key is configured: logs the message instead of sending it.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        logger.LogWarning("Resend is not configured; email '{Subject}' was not sent. Body: {Body}", email.Subject, email.HtmlBody);
        return Task.CompletedTask;
    }
}
