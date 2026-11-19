using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Nodus.Tests.Integration;

public sealed class MockEmailTests : IAsyncLifetime
{
    private const string Code = "246810";
    private readonly TestApp _app = new();

    public MockEmailTests()
    {
        _app.Settings["ServiceMode:Email"] = "Mock";
        _app.Settings["EmailOtp:MockCode"] = Code;
        _app.Settings["EmailOptions:Smtp:Host"] = "";
    }

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();
    private static string Email() => $"mock-{Guid.NewGuid():N}@nodus.test";

    [Fact]
    public async Task Fixed_code_links_email_and_signs_into_the_same_account_without_sending()
    {
        var user = await _app.CreateUserAsync();
        var email = Email();
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        var hash = await _app.WithDbAsync(db => db.EmailVerificationCodes.Where(c => c.Email == email).Select(c => c.CodeHash).SingleAsync());
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(Code, hash);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code })).StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(2));
        var client = _app.CreateClient();
        await client.PostJsonAsync("/v1/auth/email/sign-in/start", new { email });
        var signIn = await client.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email, code = Code });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(user.Id, (await signIn.ReadJsonAsync()).GetProperty("user_id").GetGuid());
        Assert.Empty(_app.Emails.Sent);
        var replay = await client.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email, code = Code });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Code_still_requires_issuance_and_is_bound_to_the_requesting_account()
    {
        var owner = await _app.CreateUserAsync();
        var other = await _app.CreateUserAsync();
        var email = Email();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code })).StatusCode);
        await owner.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code })).StatusCode);
        // A consumed linking code cannot be reused for a sign-in that was never requested.
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.CreateClient().PostJsonAsync("/v1/auth/email/sign-in/verify", new { email, code = Code })).StatusCode);
    }

    [Fact]
    public async Task Expired_fixed_code_is_rejected()
    {
        var user = await _app.CreateUserAsync();
        var email = Email();
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        _app.Time.Advance(TimeSpan.FromMinutes(11));
        // Refresh the access token through the normal endpoint is unnecessary: it lasts 15 minutes.
        var result = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task Wrong_attempts_exhaust_the_fixed_code()
    {
        var user = await _app.CreateUserAsync();
        var email = Email();
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = Code })).StatusCode);
    }

    [Fact]
    public async Task Mock_keeps_cooldown_and_hourly_limits()
    {
        var user = await _app.CreateUserAsync();
        var email = Email();
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        Assert.Equal(1, await _app.WithDbAsync(db => db.EmailVerificationCodes.CountAsync(c => c.Email == email)));
        for (var i = 0; i < 6; i++)
        {
            _app.Time.Advance(TimeSpan.FromMinutes(2));
            await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        }
        Assert.Equal(5, await _app.WithDbAsync(db => db.EmailVerificationCodes.CountAsync(c => c.Email == email)));
        Assert.Empty(_app.Emails.Sent);
    }

    [Fact]
    public async Task Mock_does_not_create_an_account_by_signing_into_an_unknown_email()
    {
        var email = Email();
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostJsonAsync("/v1/auth/email/sign-in/start", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email, code = Code })).StatusCode);
        Assert.Equal(0, await _app.WithDbAsync(db => db.EmailVerificationCodes.CountAsync(c => c.Email == email)));
    }

    [Fact]
    public async Task Server_mode_and_code_are_not_in_the_public_app_config()
    {
        var json = await (await _app.CreateClient().GetAsync("/v1/app-config")).ReadJsonAsync();
        Assert.DoesNotContain(Code, json.GetRawText());
        Assert.False(json.GetProperty("values").TryGetProperty("service_mode", out _));
        Assert.False(json.GetProperty("values").TryGetProperty("mock_code", out _));
    }
}

public sealed class LiveEmailModeTests
{
    [Fact]
    public async Task Live_still_sends_a_generated_code_when_mock_code_is_configured()
    {
        await using var app = new TestApp();
        app.Settings["EmailOtp:MockCode"] = "246810";
        await app.InitializeAsync();
        var user = await app.CreateUserAsync();
        var email = $"live-{Guid.NewGuid():N}@nodus.test";
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        Assert.Single(app.Emails.Sent);
        var result = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }
}

public sealed class ProductionMockEmailTests
{
    [Fact]
    public async Task Production_starts_and_confirms_email_without_an_smtp_host()
    {
        await using var app = new TestApp { EnvironmentName = Environments.Production };
        app.Settings["ServiceMode:Email"] = "Mock";
        app.Settings["EmailOtp:MockCode"] = "246810";
        app.Settings["EmailOptions:Smtp:Host"] = "";
        await app.InitializeAsync();
        var user = await app.CreateUserAsync();
        var email = $"production-mock-{Guid.NewGuid():N}@nodus.test";
        Assert.Equal(HttpStatusCode.Accepted, (await user.Client.PostJsonAsync("/v1/auth/email/start", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = "246810" })).StatusCode);
        Assert.Empty(app.Emails.Sent);
    }
}
