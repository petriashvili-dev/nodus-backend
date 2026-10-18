using System.ComponentModel.DataAnnotations;
using System.Net;
using Nodus.Api.Authentication;

namespace Nodus.Tests.Integration;

/// <summary>App Review demo account: a fixed code for configured addresses, nothing emailed, usual limits.</summary>
public class ReviewAccountTests : IAsyncLifetime
{
    private const string ReviewEmail = "review@nodus.app";
    private const string ReviewCode = "246810";
    private readonly TestApp _app = new();

    public ReviewAccountTests()
    {
        _app.Settings["EmailOtp:ReviewEmails:0"] = " Review@Nodus.app ";
        _app.Settings["EmailOtp:ReviewCode"] = ReviewCode;
    }

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();

    private static string Key() => Guid.NewGuid().ToString();

    private async Task<TestUser> LinkReviewAccountAsync()
    {
        var user = await _app.CreateUserAsync();
        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email = ReviewEmail });
        var linked = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email = ReviewEmail, code = ReviewCode });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(2)); // past the resend cooldown shared by all codes of an address
        return user;
    }

    [Fact]
    public async Task Review_account_is_linked_and_signed_in_with_the_fixed_code_without_email()
    {
        var owner = await LinkReviewAccountAsync();
        var reviewer = _app.CreateClient();

        await reviewer.PostJsonAsync("/v1/auth/email/sign-in/start", new { email = ReviewEmail });
        var signIn = await reviewer.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email = ReviewEmail, code = ReviewCode });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(owner.Id, (await signIn.ReadJsonAsync()).GetProperty("user_id").GetGuid());
        Assert.DoesNotContain(_app.Emails.Sent, e => e.To == ReviewEmail);
    }

    [Fact]
    public async Task Other_addresses_still_get_random_codes_by_email()
    {
        const string email = "someone@example.com";
        var user = await _app.CreateUserAsync();

        await user.Client.PostJsonAsync("/v1/auth/email/start", new { email });
        var withReviewCode = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = ReviewCode });
        var withSentCode = await user.Client.PostJsonAsync("/v1/auth/email/verify", new { email, code = _app.Emails.LastCodeFor(email) });

        Assert.Equal(HttpStatusCode.BadRequest, withReviewCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withSentCode.StatusCode);
    }

    [Fact]
    public async Task Fixed_code_keeps_the_attempt_limit()
    {
        await LinkReviewAccountAsync();
        var client = _app.CreateClient();
        await client.PostJsonAsync("/v1/auth/email/sign-in/start", new { email = ReviewEmail });

        for (var i = 0; i < 5; i++)
            await client.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email = ReviewEmail, code = "000000" });
        var afterLimit = await client.PostJsonAsync("/v1/auth/email/sign-in/verify", new { email = ReviewEmail, code = ReviewCode });

        Assert.Equal("invalid_code", (await afterLimit.ReadJsonAsync()).Code());
    }
}

public class ReviewAccountOptionsTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("12345", false)]
    [InlineData("12345a", false)]
    [InlineData("246810", true)]
    public void Review_code_must_be_six_digits_when_review_emails_are_set(string code, bool valid)
    {
        var review = new EmailOtpOptions { HashPepper = new string('p', 16), ReviewEmails = ["review@nodus.app"], ReviewCode = code };
        var ordinary = new EmailOtpOptions { HashPepper = new string('p', 16) };

        Assert.Equal(valid, IsValid(review));
        Assert.True(IsValid(ordinary));
    }

    private static bool IsValid(EmailOtpOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
