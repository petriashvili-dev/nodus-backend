using System.Net;
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
    public ResendProxyOptions Proxy { get; init; } = new();
}

/// <summary>
/// Outgoing proxy for Resend calls only (the host may have no direct route to the Resend API).
/// Empty <see cref="Url"/> — direct connection. Credentials are optional and come from environment.
/// </summary>
public sealed class ResendProxyOptions
{
    public string Url { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    public bool IsValid() => !Enabled || Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "socks5" && string.IsNullOrEmpty(uri.UserInfo);

    public SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) };
        if (!Enabled) return handler;

        var proxy = new WebProxy(Url);
        if (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password))
            proxy.Credentials = new NetworkCredential(Username, Password);
        handler.Proxy = proxy;
        handler.UseProxy = true;
        return handler;
    }
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

/// <summary>
/// Fallback when no Resend key is configured (never in Production, see <c>AddNodusAuthentication</c>). Bodies carry
/// one-time codes, so they are logged only in Development, where a developer needs the code to sign in locally.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        if (environment.IsDevelopment())
            logger.LogWarning("Resend is not configured; email '{Subject}' was not sent. Body (Development only): {Body}",
                email.Subject, email.HtmlBody);
        else
            logger.LogWarning("Resend is not configured; email '{Subject}' was not sent", email.Subject);
        return Task.CompletedTask;
    }
}
