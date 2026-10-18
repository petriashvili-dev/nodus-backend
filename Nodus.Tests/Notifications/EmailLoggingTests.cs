using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nodus.Api;
using Nodus.Api.Infrastructure.Resend;

namespace Nodus.Tests.Notifications;

/// <summary>One-time codes must not reach logs outside Development; Production refuses to run without Resend.</summary>
public class EmailLoggingTests
{
    private const string Body = "<p>Ваш код: <strong>123456</strong></p>";

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", false)]
    [InlineData("Testing", false)]
    public async Task Email_bodies_are_logged_only_in_development(string environment, bool bodyLogged)
    {
        var logger = new CapturingLogger();
        var sender = new LoggingEmailSender(logger, new Environment(environment));

        await sender.SendAsync(new OutgoingEmail("a@b.cd", "Nodus: код входа", Body), CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Nodus: код входа", message);
        Assert.Equal(bodyLogged, message.Contains("123456"));
        Assert.DoesNotContain("a@b.cd", message);
    }

    [Fact]
    public void Production_without_a_Resend_key_fails_to_start()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NodusApplication.Build([], builder =>
        {
            builder.Environment.EnvironmentName = Environments.Production;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused",
                ["Resend:ApiKey"] = ""
            });
        }));

        Assert.Contains("Resend:ApiKey", error.Message);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Nodus.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger<LoggingEmailSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
