// Closed-loop load test of the non-LLM API surface.
// Usage: dotnet run -c Release --project Nodus.LoadTests -- <base-url> [virtual-users=50] [seconds=60]
// Each virtual user registers anonymously, then repeatedly creates a node, edits it, unties it, runs Delta Sync and
// loads the feed. Reports p50/p95/p99 per operation; the target is p95 < 300 ms (requirements §11).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:5258");
var users = args.Length > 1 ? int.Parse(args[1]) : 50;
var duration = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 60);

using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = users * 2 }) { BaseAddress = baseUrl };
var samples = new ConcurrentDictionary<string, ConcurrentBag<double>>();
var errors = new ConcurrentDictionary<string, int>();
using var stop = new CancellationTokenSource(duration);

async Task<HttpResponseMessage> Timed(string operation, Func<Task<HttpResponseMessage>> call)
{
    var started = Stopwatch.GetTimestamp();
    var response = await call();
    samples.GetOrAdd(operation, _ => []).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    if (!response.IsSuccessStatusCode) errors.AddOrUpdate($"{operation} {(int)response.StatusCode}", 1, (_, n) => n + 1);
    return response;
}

HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null)
{
    var request = new HttpRequestMessage(method, url);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (method != HttpMethod.Get) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    if (body is not null) request.Content = JsonContent.Create(body);
    return request;
}

async Task VirtualUser()
{
    var auth = await Timed("auth.anonymous", () => http.PostAsync("/v1/auth/anonymous", null));
    var token = (await auth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    long cursor = 0;
    var i = 0;
    while (!stop.IsCancellationRequested)
    {
        var id = Guid.NewGuid();
        var recurring = i++ % 3 == 0;
        object node = recurring
            ? new { id, type = "recurring", title = "Зарядка", timezone = "Europe/Moscow",
                recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { "07:30" } } }
            : new { id, type = "one_time", title = "Купить хлеб", timezone = "Europe/Moscow", date = "2026-10-20", time = "18:00" };
        await Timed("nodes.create", () => http.SendAsync(Request(HttpMethod.Post, "/v1/nodes", token, node)));
        await Timed("nodes.patch", () => http.SendAsync(Request(HttpMethod.Patch, $"/v1/nodes/{id}", token, recurring
            ? new { expected_version = 1, title = "Утренняя зарядка", timezone = "Europe/Moscow",
                recurrence = new { frequency = "daily", start_date = "2026-10-01", times = new[] { "08:00" } } }
            : new { expected_version = 1, title = "Купить хлеб и молоко", timezone = "Europe/Moscow", date = "2026-10-20" })));
        var key = recurring ? $"g0-{DateTime.UtcNow.AddDays(1):yyyyMMdd}-0" : "once";
        await Timed("occurrence.untie", () => http.SendAsync(Request(HttpMethod.Put, $"/v1/nodes/{id}/occurrences/{key}/untie", token)));
        var sync = await Timed("sync.delta", () => http.SendAsync(Request(HttpMethod.Get, $"/v1/sync?cursor={cursor}", token)));
        if (sync.IsSuccessStatusCode) cursor = (await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("next_cursor").GetInt64();
        await Timed("feed", () => http.SendAsync(Request(HttpMethod.Get, "/v1/feed", token)));
    }
}

Console.WriteLine($"{users} virtual users against {baseUrl} for {duration.TotalSeconds:0}s…");
var total = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, users).Select(_ => Task.Run(VirtualUser)));

static double Percentile(double[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

Console.WriteLine($"{"operation",-18}{"count",8}{"rps",8}{"p50 ms",9}{"p95 ms",9}{"p99 ms",9}");
foreach (var (operation, bag) in samples.OrderBy(s => s.Key))
{
    var sorted = bag.Order().ToArray();
    Console.WriteLine($"{operation,-18}{sorted.Length,8}{sorted.Length / total.Elapsed.TotalSeconds,8:0}" +
                      $"{Percentile(sorted, .5),9:0.0}{Percentile(sorted, .95),9:0.0}{Percentile(sorted, .99),9:0.0}");
}

foreach (var (error, count) in errors) Console.WriteLine($"error {error}: {count}");
