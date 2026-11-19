using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Nodus.Api.Infrastructure.Email;

namespace Nodus.Tests.Notifications;

public sealed class SmtpEmailSenderTests
{
    private static readonly OutgoingEmail Email = new("recipient@gmail.com", "Код входа Nodus",
        "<html><head><style>hidden</style></head><body><p>Ваш код: <b>123456</b></p></body></html>");

    private static SmtpEmailSender Sender(int port, int timeoutSeconds = 5, string security = "None") => new(
        Options.Create(new EmailOptions { Smtp = new SmtpOptions
            { Host = "127.0.0.1", Port = port, TimeoutSeconds = timeoutSeconds, Security = security } }),
        () => new SmtpClient(), NullLogger<SmtpEmailSender>.Instance);

    [Fact]
    public async Task Real_smtp_uses_configured_envelope_and_utf8_multipart_without_auth_or_starttls()
    {
        await using var server = new SmtpTestServer();
        await Sender(server.Port).SendAsync(Email, CancellationToken.None);
        Assert.Contains(server.Commands, c => c.StartsWith("MAIL FROM:<no-reply@nodus.app>", StringComparison.Ordinal));
        Assert.Contains(server.Commands, c => c.StartsWith("RCPT TO:<recipient@gmail.com>", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH") || c.StartsWith("STARTTLS"));
        using var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(Assert.Single(server.Messages))));
        Assert.Equal("Nodus", message.From.Mailboxes.Single().Name);
        Assert.Equal("no-reply@nodus.app", message.From.Mailboxes.Single().Address);
        Assert.Equal(Email.Subject, message.Subject);
        Assert.Equal(Email.HtmlBody, message.HtmlBody);
        Assert.Contains("Ваш код: 123456", message.TextBody);
        Assert.DoesNotContain("hidden", message.TextBody);
        Assert.IsType<MultipartAlternative>(message.Body);
        Assert.All(((Multipart)message.Body).OfType<TextPart>(), p => Assert.Equal("utf-8", p.ContentType.Charset));
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
    }

    [Theory]
    [InlineData(421, true)]
    [InlineData(450, true)]
    [InlineData(550, false)]
    [InlineData(553, false)]
    public async Task Failure_does_not_expose_the_server_response(int status, bool transient)
    {
        await using var server = new SmtpTestServer([status]);
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(server.Port).SendAsync(Email, CancellationToken.None));
        Assert.Equal("smtp_command", error.Code);
        Assert.Equal(status, error.SmtpStatusCode);
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("recipient@gmail.com", error.ToString());
        Assert.DoesNotContain("secret-server-detail", error.ToString());
        Assert.DoesNotContain("123456", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task Dropped_quit_does_not_turn_an_accepted_email_into_failure()
    {
        await using var server = new SmtpTestServer(dropOnQuit: true);
        await Sender(server.Port).SendAsync(Email, CancellationToken.None);
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task Required_starttls_never_falls_back_to_plaintext()
    {
        await using var server = new SmtpTestServer(advertiseStartTls: false);
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(server.Port, security: "StartTls")
            .SendAsync(Email, CancellationToken.None));
        Assert.Equal("smtp_configuration", error.Code);
        Assert.Empty(server.Messages);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL FROM"));
    }

    [Fact]
    public async Task Whole_send_timeout_is_a_transient_failure()
    {
        await using var server = new SmtpTestServer(silent: true);
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(server.Port, timeoutSeconds: 1)
            .SendAsync(Email, CancellationToken.None));
        Assert.True(error.IsTransient);
        Assert.Equal("smtp_timeout", error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        await using var server = new SmtpTestServer(silent: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(server.Port).SendAsync(Email, cancel.Token));
    }

    [Fact]
    public async Task Unavailable_server_returns_a_safe_transient_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(port).SendAsync(Email, CancellationToken.None));
        Assert.Equal("smtp_unavailable", error.Code);
        Assert.True(error.IsTransient);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("local")]
    public async Task Invalid_recipient_fails_before_connecting(string recipient)
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(1)
            .SendAsync(Email with { To = recipient }, CancellationToken.None));
        Assert.Equal("invalid_email", error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public void Configuration_registers_smtp_and_gives_each_send_its_own_client()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<SmtpEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        var factory = provider.GetRequiredService<Func<ISmtpClient>>();
        using var first = factory();
        using var second = factory();
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData("EmailOptions:Smtp:Host", "smtp://mail.local")]
    [InlineData("EmailOptions:Smtp:Host", "mail local")]
    [InlineData("EmailOptions:Smtp:Port", "0")]
    [InlineData("EmailOptions:Smtp:Port", "65536")]
    [InlineData("EmailOptions:Smtp:TimeoutSeconds", "0")]
    [InlineData("EmailOptions:Smtp:Security", "Auto")]
    [InlineData("EmailOptions:Smtp:Authentication", "true")]
    [InlineData("EmailOptions:FromAddress", "Nodus <no-reply@nodus.app>")]
    [InlineData("EmailOptions:FromName", "Nodus\r\nInjected: header")]
    public void Invalid_configuration_is_rejected(string key, string value)
    {
        using var provider = Services(key, value).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
    }

    private static ServiceCollection Services(string? key = null, string? value = null)
    {
        var settings = new Dictionary<string, string?> { ["EmailOptions:Smtp:Host"] = "mail.local" };
        if (key is not null) settings[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddEmailTransport(config, new Environment());
        return services;
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Nodus.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
