using System.Net;

namespace Nodus.Tests.Integration;

public class MetricsEndpointTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public MetricsEndpointTests()
    {
        _app.Settings["Observability:PrometheusEnabled"] = "true";
        _app.Settings["Observability:MetricsPort"] = "9464";
    }

    public Task InitializeAsync() => _app.InitializeAsync();
    public Task DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task Metrics_answer_only_on_the_internal_port()
    {
        var user = await _app.CreateUserAsync();
        await user.Client.GetAsync("/v1/sync?cursor=0"); // records nodus.sync.requests
        var client = _app.CreateClient();

        var publicPort = await client.GetAsync("http://localhost/metrics");
        var internalPort = await client.GetAsync("http://localhost:9464/metrics");

        Assert.Equal(HttpStatusCode.NotFound, publicPort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, internalPort.StatusCode);
        Assert.Contains("nodus_sync_requests", await internalPort.Content.ReadAsStringAsync());
    }
}
