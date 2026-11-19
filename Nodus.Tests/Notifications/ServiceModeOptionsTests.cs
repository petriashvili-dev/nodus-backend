using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nodus.Api.Infrastructure.Email;
using Nodus.Api.Infrastructure.ServiceModes;

namespace Nodus.Tests.Notifications;

public sealed class ServiceModeOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("mail.local")]
    public async Task Production_mock_needs_no_smtp_and_does_not_register_a_network_client(string host)
    {
        using var provider = Services("Mock", "246810", host).BuildServiceProvider();
        Assert.Equal(EmailServiceMode.Mock, provider.GetRequiredService<IOptions<ServiceModeOptions>>().Value.Email);
        var sender = Assert.IsType<MockEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.Null(provider.GetService<Func<ISmtpClient>>());
        await sender.SendAsync(new OutgoingEmail("unused@nodus.test", "test", "123456"), CancellationToken.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("１２３４５６")]
    public void Mock_requires_an_explicit_six_ascii_digit_code(string code)
    {
        using var provider = Services("Mock", code).BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ServiceModeOptions>>().Value);
        Assert.Contains("EmailOtp:MockCode", error.Message);
        if (code.Length > 0) Assert.DoesNotContain(code, error.Message);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("999")]
    public void Unsupported_mode_fails_instead_of_silently_disabling_email(string mode) =>
        Assert.Throws<InvalidOperationException>(() => Services(mode, "246810"));

    [Fact]
    public void Production_live_requires_smtp_even_when_mock_code_is_configured() =>
        Assert.Throws<InvalidOperationException>(() => Services("Live", "246810"));

    [Fact]
    public void Production_live_registers_smtp()
    {
        using var provider = Services("Live", "246810", "mail.local").BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(EmailServiceMode.Live, provider.GetRequiredService<IOptions<ServiceModeOptions>>().Value.Email);
        Assert.IsType<SmtpEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
    }

    private static ServiceCollection Services(string mode, string code, string host = "")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceMode:Email"] = mode,
            ["EmailOtp:MockCode"] = code,
            ["EmailOptions:Smtp:Host"] = host
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddEmailTransport(config, new Production());
        return services;
    }

    private sealed class Production : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Nodus.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
