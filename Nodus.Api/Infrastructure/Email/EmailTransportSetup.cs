using MailKit.Net.Smtp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nodus.Api.Authentication;
using Nodus.Api.Infrastructure.ServiceModes;

namespace Nodus.Api.Infrastructure.Email;

public static class EmailTransportSetup
{
    public static IServiceCollection AddEmailTransport(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var mode = configuration.GetValue<EmailServiceMode>($"{ServiceModeOptions.Section}:Email");
        if (!Enum.IsDefined(mode))
            throw new InvalidOperationException("ServiceMode:Email must be Live or Mock.");
        services.AddOptions<ServiceModeOptions>().BindConfiguration(ServiceModeOptions.Section)
            .Validate(o => Enum.IsDefined(o.Email), "ServiceMode:Email must be Live or Mock.")
            .Validate(o => o.Email != EmailServiceMode.Mock || EmailOtpOptions.IsSixDigitCode(configuration["EmailOtp:MockCode"]),
                "EmailOtp:MockCode must be six digits when ServiceMode:Email=Mock.")
            .ValidateOnStart();
        var mock = mode == EmailServiceMode.Mock;
        var configured = !string.IsNullOrWhiteSpace(configuration[$"{EmailOptions.Section}:Smtp:Host"]);
        if (!mock && !configured && environment.IsProduction())
            throw new InvalidOperationException("EmailOptions:Smtp:Host is required in Production to deliver email codes.");

        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section)
            .Validate(o => o.HasValidSender(), "EmailOptions requires a mailbox FromAddress and a FromName without newlines.")
            .Validate(o => mock || !configured || o.Smtp is not null && o.Smtp.IsValid(),
                "EmailOptions:Smtp requires Host, Port 1..65535, Security None/StartTls/SslOnConnect, TimeoutSeconds 1..300 and credentials when Authentication=true.")
            .ValidateOnStart();
        if (mock)
            services.TryAddSingleton<IEmailSender, MockEmailSender>();
        else if (configured)
        {
            // MailKit clients are not thread-safe; every send owns one connection.
            services.TryAddSingleton<Func<ISmtpClient>>(_ => () => new SmtpClient());
            services.TryAddScoped<IEmailSender, SmtpEmailSender>();
        }
        else services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        return services;
    }
}
