using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api.Infrastructure.Resend;
using Resend;

namespace Nodus.Tests.Integration;

/// <summary>The Resend HTTP client goes through the configured proxy; a local listener plays the proxy.</summary>
public class ResendProxyTests : IAsyncLifetime
{
    private readonly TcpListener _proxy = new(IPAddress.Loopback, 0);
    private readonly TestApp _app = new();

    public ResendProxyTests()
    {
        _proxy.Start();
        _app.Settings["Resend:ApiKey"] = "re_test_key";
        _app.Settings["Resend:Proxy:Url"] = $"http://127.0.0.1:{((IPEndPoint)_proxy.LocalEndpoint).Port}";
        _app.Settings["Resend:Proxy:Username"] = "nodus";
        _app.Settings["Resend:Proxy:Password"] = "secret";
    }

    public Task InitializeAsync() => _app.InitializeAsync();

    public async Task DisposeAsync()
    {
        _proxy.Stop();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Resend_requests_are_sent_through_the_proxy_with_credentials()
    {
        var resend = _app.Services.CreateScope().ServiceProvider.GetRequiredService<IResend>();
        var message = new EmailMessage { From = "Nodus <no-reply@nodus.app>", Subject = "s", HtmlBody = "b" };
        message.To.Add("someone@example.com");

        using var cancel = new CancellationTokenSource();
        var send = resend.EmailSendAsync(message, cancel.Token);
        using var client = await _proxy.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var request = await ReadHeadAsync(client.GetStream());
        // The proxy demands authentication; the handler repeats CONNECT with credentials.
        await client.GetStream().WriteAsync(
            "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"p\"\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        var authenticated = await ReadHeadAsync(client.GetStream());
        // Nothing answers after that; stop the request instead of waiting for the HTTP timeout.
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await send);

        Assert.StartsWith("CONNECT api.resend.com:443", request);
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("nodus:secret"));
        Assert.Contains($"Proxy-Authorization: Basic {credentials}", authenticated);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) &&
               await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) > 0)
            head.Append((char)buffer[0]);
        return head.ToString();
    }
}

public class ResendProxyOptionsTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("http://proxy.local:3128", true)]
    [InlineData("socks5://proxy.local:1080", true)]
    [InlineData("proxy.local:3128", false)]
    [InlineData("ftp://proxy.local", false)]
    [InlineData("http://user:pass@proxy.local:3128", false)]
    public void Proxy_url_must_be_absolute_without_credentials(string url, bool valid) =>
        Assert.Equal(valid, new ResendProxyOptions { Url = url }.IsValid());

    [Fact]
    public void Without_url_the_handler_connects_directly()
    {
        using var direct = new ResendProxyOptions().CreateHandler();
        using var proxied = new ResendProxyOptions { Url = "http://proxy.local:3128", Username = "u", Password = "p" }.CreateHandler();

        Assert.Null(direct.Proxy);
        var proxy = Assert.IsType<WebProxy>(proxied.Proxy);
        Assert.Equal(new Uri("http://proxy.local:3128"), proxy.Address);
        Assert.Equal("u", ((NetworkCredential)proxy.Credentials!).UserName);
    }
}
