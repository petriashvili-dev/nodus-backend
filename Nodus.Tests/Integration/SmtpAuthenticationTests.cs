using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;
using Nodus.Api.Infrastructure.Email;
using Nodus.Tests.Notifications;

namespace Nodus.Tests.Integration;

public sealed class SmtpAuthenticationTests
{
    [Theory]
    [InlineData(250)]
    [InlineData(550)]
    public async Task Email_linking_uses_smtp_and_only_saves_the_code_after_acceptance(int smtpStatus)
    {
        await using var server = new SmtpTestServer([smtpStatus]);
        await using var app = new TestApp
        {
            ExtraServices = services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender, SmtpEmailSender>();
            }
        };
        app.Settings["EmailOptions:Smtp:Host"] = "127.0.0.1";
        app.Settings["EmailOptions:Smtp:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        app.Settings["EmailOptions:Smtp:Security"] = "None";
        app.Settings["EmailOptions:Smtp:Authentication"] = "false";
        await app.InitializeAsync();
        var user = await app.CreateUserAsync();
        var email = $"smtp-{Guid.NewGuid():N}@nodus.test";
        var response = await user.Client.PostAsJsonAsync("/v1/auth/email/start", new { email });
        if (smtpStatus != 250)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, await app.WithDbAsync(db => db.EmailVerificationCodes.CountAsync(c => c.Email == email)));
            Assert.Empty(server.Messages);
            return;
        }
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(Assert.Single(server.Messages))));
        Assert.Equal(email, message.To.Mailboxes.Single().Address);
        var text = Assert.IsType<string>(message.TextBody);
        var code = Regex.Match(text, @"\b\d{6}\b").Value;
        Assert.Equal(6, code.Length);
        var verify = await user.Client.PostAsJsonAsync("/v1/auth/email/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.True(await app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == user.Id && u.Email == email)));
    }
}
