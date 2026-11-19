using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace Nodus.Api.Infrastructure.Email;

public sealed record OutgoingEmail(string To, string Subject, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct);
}

/// <summary>No transport and no payload logging, even when SMTP is configured.</summary>
public sealed class MockEmailSender : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>Safe for infrastructure logs: no server response, addresses or message content.</summary>
public sealed class EmailDeliveryException(string code, bool isTransient, int? smtpStatusCode = null)
    : Exception($"Email delivery failed: {code}.")
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = isTransient;
    public int? SmtpStatusCode { get; } = smtpStatusCode;
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, Func<ISmtpClient> clientFactory,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    internal MimeMessage CreateMessage(OutgoingEmail email)
    {
        var settings = options.Value;
        var from = new MailboxAddress(Encoding.UTF8, settings.FromName, settings.FromAddress);
        var to = MailboxAddress.Parse(email.To);
        if (string.IsNullOrWhiteSpace(to.Domain)) throw new FormatException("Recipient must include a domain.");
        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = email.Subject;
        message.MessageId = MimeUtils.GenerateMessageId(from.Domain);
        message.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = ToPlainText(email.HtmlBody) },
            new TextPart("html") { Text = email.HtmlBody }
        };
        return message;
    }

    public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var message = CreateMessage(email);
            var settings = options.Value.Smtp;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var client = clientFactory();
            client.Timeout = checked(settings.TimeoutSeconds * 1000);
            var security = settings.Security switch
            {
                "None" => SecureSocketOptions.None,
                "StartTls" => SecureSocketOptions.StartTls,
                "SslOnConnect" => SecureSocketOptions.SslOnConnect,
                _ => throw new ArgumentException("Invalid SMTP security.")
            };
            await client.ConnectAsync(settings.Host, settings.Port, security, timeout.Token);
            if (settings.Authentication)
                await client.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
            await client.SendAsync(message, (MailboxAddress)message.From[0], message.To.Mailboxes, timeout.Token);
            // A 250 acceptance is final; a broken QUIT must not trigger another send.
            try { await client.DisconnectAsync(true, timeout.Token); }
            catch (Exception ex) when (ex is IOException or SmtpProtocolException or SmtpCommandException or OperationCanceledException)
            {
                logger.LogDebug("SMTP disconnect failed after acceptance: {ErrorType}", ex.GetType().Name);
            }
            logger.LogInformation("SMTP email accepted in {ElapsedMs} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SmtpCommandException ex)
        {
            var status = (int)ex.StatusCode;
            throw new EmailDeliveryException("smtp_command", status is >= 400 and < 500, status);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or AuthenticationException
                                       or SslHandshakeException or ServiceNotAuthenticatedException
                                       or NotSupportedException or OptionsValidationException)
        {
            throw new EmailDeliveryException(ex is FormatException ? "invalid_email" : "smtp_configuration", false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException
                                       or IOException or SmtpProtocolException)
        {
            throw new EmailDeliveryException(ex is OperationCanceledException or TimeoutException
                ? "smtp_timeout" : "smtp_unavailable", true);
        }
    }

    private static string ToPlainText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var text = new StringBuilder();
        var hiddenDepth = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                if (tag.Id is HtmlTagId.Head or HtmlTagId.Style or HtmlTagId.Script)
                    hiddenDepth = Math.Max(0, hiddenDepth + (tag.IsEndTag ? -1 : 1));
                else if (hiddenDepth == 0 && tag.Name.ToLowerInvariant() is "br" or "p" or "div" or "tr" or "li")
                    text.AppendLine();
            }
            else if (hiddenDepth == 0 && token is HtmlDataToken data && token.Kind == HtmlTokenKind.Data)
                text.Append(data.Data);
        }
        return text.ToString().Trim();
    }
}

/// <summary>Local fallback only; OTP bodies are logged exclusively in Development.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (environment.IsDevelopment())
            logger.LogWarning("SMTP is not configured; email '{Subject}' was not sent. Body (Development only): {Body}",
                email.Subject, email.HtmlBody);
        else logger.LogWarning("SMTP is not configured; email '{Subject}' was not sent", email.Subject);
        return Task.CompletedTask;
    }
}
