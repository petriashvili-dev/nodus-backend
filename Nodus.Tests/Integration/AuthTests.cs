using System.Net;

namespace Nodus.Tests.Integration;

public class AuthTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Anonymous_user_gets_tokens_and_profile()
    {
        var user = await app.CreateUserAsync();

        var me = await (await user.Client.GetAsync("/v1/me")).ReadJsonAsync();

        Assert.Equal(user.Id, me.GetProperty("id").GetGuid());
        Assert.True(me.GetProperty("is_anonymous").GetBoolean());
    }

    [Fact]
    public async Task Protected_endpoints_reject_missing_or_forged_tokens()
    {
        var missing = await app.CreateClient().GetAsync("/v1/me");
        var forged = await app.CreateClient("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl").GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("unauthorized", (await missing.ReadJsonAsync()).Code());
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_after_grace_revokes_the_family()
    {
        var user = await app.CreateUserAsync();
        var anonymous = app.CreateClient();

        var first = await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = user.RefreshToken });
        var rotated = (await first.ReadJsonAsync()).GetProperty("refresh_token").GetString();
        app.Time.Advance(TimeSpan.FromMinutes(5));
        var reused = await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = user.RefreshToken });
        var afterTheft = await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = rotated });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(user.RefreshToken, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Lost_refresh_response_can_be_retried_within_grace_period()
    {
        var user = await app.CreateUserAsync();
        var anonymous = app.CreateClient();

        await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = user.RefreshToken });
        var retry = await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = user.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var user = await app.CreateUserAsync();
        var anonymous = app.CreateClient();

        await anonymous.PostJsonAsync("/v1/auth/logout", new { refresh_token = user.RefreshToken });
        var refresh = await anonymous.PostJsonAsync("/v1/auth/refresh", new { refresh_token = user.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Linking_email_keeps_the_same_user_and_allows_sign_in_elsewhere()
    {
        var user = await app.CreateUserAsync();
        var email = $"{Guid.NewGuid():N}@example.com";

        Assert.Equal(HttpStatusCode.Accepted,
            (await user.Client.PostJsonAsync("/v1/auth/email/start", new { email = email.ToUpperInvariant() })).StatusCode);
        var linked = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });
        var profile = await linked.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal((user.Id, false, email),
            (profile.GetProperty("id").GetGuid(), profile.GetProperty("is_anonymous").GetBoolean(),
                profile.GetProperty("email").GetString()));

        app.Time.Advance(TimeSpan.FromMinutes(2));
        var device = app.CreateClient();
        await device.PostJsonAsync("/v1/auth/email/sign-in/start", new { email });
        var signIn = await device.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email, code = app.Emails.LastCodeFor(email) });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(user.Id, (await signIn.ReadJsonAsync()).GetProperty("user_id").GetGuid());
    }

    [Fact]
    public async Task Email_owned_by_another_account_is_not_linked()
    {
        var owner = await app.CreateUserAsync();
        var other = await app.CreateUserAsync();
        var email = $"{Guid.NewGuid():N}@example.com";
        await owner.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        await owner.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });
        app.Time.Advance(TimeSpan.FromMinutes(2));

        await other.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        var response = await other.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = app.Emails.LastCodeFor(email) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_already_linked", (await response.ReadJsonAsync()).Code());
    }

    [Fact]
    public async Task Sign_in_does_not_reveal_unknown_emails_and_wrong_codes_are_limited()
    {
        var unknown = $"{Guid.NewGuid():N}@example.com";
        var client = app.CreateClient();

        var start = await client.PostJsonAsync("/v1/auth/email/sign-in/start", new { email = unknown });

        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.DoesNotContain(app.Emails.Sent, e => e.To == unknown);

        var user = await app.CreateUserAsync();
        var email = $"{Guid.NewGuid():N}@example.com";
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        var code = app.Emails.LastCodeFor(email);
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
            await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = wrong });
        var correctTooLate = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code });

        Assert.Equal("invalid_code", (await correctTooLate.ReadJsonAsync()).Code());
    }
}
