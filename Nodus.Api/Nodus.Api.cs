using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace Nodus.Api;

public sealed class NodusDbContext(DbContextOptions<NodusDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<LiveActivityPushSchedule> LiveActivityPushSchedules => Set<LiveActivityPushSchedule>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionRestoreCode> SubscriptionRestoreCodes => Set<SubscriptionRestoreCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Email, x.DeviceId }).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DeviceId).HasMaxLength(256);
            e.Property(x => x.LiveActivityPushToken).HasMaxLength(4096);
        });
        b.Entity<LiveActivityPushSchedule>(e =>
        {
            e.ToTable("live_activity_push_schedules");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PushAt);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProviderPaymentId).IsUnique();
            e.HasIndex(x => new { x.Email, x.DeviceDbId });
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceDbId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PaymentId).IsUnique();
            e.HasIndex(x => new { x.Email, x.DeviceId });
            e.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SubscriptionRestoreCode>(e =>
        {
            e.ToTable("subscription_restore_codes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Email, x.CreatedAt });
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.CodeHash).HasMaxLength(128);
        });
    }
}

public sealed class NodusDbContextFactory : IDesignTimeDbContextFactory<NodusDbContext>
{
    public NodusDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NodusDbContext>()
        .UseNpgsql("Host=localhost;Port=5432;Database=nodus;Username=nodus;Password=nodus").Options);
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string DeviceId { get; set; }
    public string? LiveActivityPushToken { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PushScheduleStatus
{
    Scheduled,
    Sent,
    Failed
}

public sealed class LiveActivityPushSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public required Device Device { get; set; }
    public required string ActivityId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset PushAt { get; set; }
    public required string PayloadJson { get; set; }
    public string? HangfireJobId { get; set; }
    public PushScheduleStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? Error { get; set; }
}

public enum PaymentStatus
{
    Pending,
    WaitingForCapture,
    Succeeded,
    Canceled
}

public sealed class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public Guid DeviceDbId { get; set; }
    public required Device Device { get; set; }
    public required string PlanCode { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public int DurationDays { get; set; }
    public PaymentStatus Status { get; set; }
    public string? ProviderPaymentId { get; set; }
    public string? ConfirmationUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string DeviceId { get; set; }
    public required string PlanCode { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid PaymentId { get; set; }
    public required Payment Payment { get; set; }
}

public sealed class SubscriptionRestoreCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public record LlmCompletionRequest(string Prompt, string? SystemPrompt = null, string? Model = null);

public record ScheduleLiveActivityRequest(
    string Email,
    string DeviceId,
    string LiveActivityPushToken,
    string ActivityId,
    DateTimeOffset StartsAt,
    JsonElement Payload);

public record CreateSubscriptionPaymentRequest(string Email, string DeviceId, string PlanCode, string ReturnUrl);

public record YooKassaWebhook(string Type, YooKassaWebhookObject Object);

public record YooKassaWebhookObject(string Id);

public record SubscriptionRestoreRequest(string Email);

public record ConfirmSubscriptionRestoreRequest(
    string Email,
    string Code,
    string DeviceId,
    string? LiveActivityPushToken = null);

public sealed class LlmOptions
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "gpt-4.1-mini";
}

public sealed class YooKassaOptions
{
    public string ShopId { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.yookassa.ru/v3/";
}

public sealed class LiveActivityPushOptions
{
    public string Endpoint { get; init; } = "";
    public string Topic { get; init; } = "";
    public string? BearerToken { get; init; }
}

public sealed class ResendOptions
{
    public string ApiKey { get; init; } = "";
    public string From { get; init; } = "";
    public int CodeExpiryMinutes { get; init; } = 10;
    public int ResendCooldownSeconds { get; init; } = 60;
    public int MaxCodesPerHour { get; init; } = 5;
    public string HashPepper { get; init; } = "";
}

public interface ILlmClient
{
    Task<JsonElement> CompleteAsync(LlmCompletionRequest request, CancellationToken ct);
}

public sealed class OpenAiCompatibleLlmClient(HttpClient http, IOptions<LlmOptions> options) : ILlmClient
{
    public async Task<JsonElement> CompleteAsync(LlmCompletionRequest request, CancellationToken ct)
    {
        var o = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl), "chat/completions"));
        message.Headers.Authorization = new("Bearer", o.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = request.Model ?? o.Model,
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt ?? "Return a valid JSON object only." },
                new { role = "user", content = request.Prompt }
            },
            response_format = new { type = "json_object" }
        });
        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
            .GetString() ?? "{}";
        return JsonDocument.Parse(content).RootElement.Clone();
    }
}

public interface ILiveActivityPushClient
{
    Task SendAsync(string token, string activityId, JsonElement payload, CancellationToken ct);
}

public sealed class LiveActivityPushClient(HttpClient http, IOptions<LiveActivityPushOptions> options)
    : ILiveActivityPushClient
{
    public async Task SendAsync(string token, string activityId, JsonElement payload, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Endpoint) || string.IsNullOrWhiteSpace(o.Topic))
            throw new InvalidOperationException("LiveActivityPush:Endpoint and Topic must be configured.");
        
        using var r = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.Endpoint), token));
        
        if (!string.IsNullOrEmpty(o.BearerToken)) 
            r.Headers.Authorization = new("Bearer", o.BearerToken);
        
        r.Headers.Add("apns-push-type", "liveactivity");
        r.Headers.Add("apns-topic", o.Topic);
        r.Headers.Add("apns-collapse-id", activityId);
        r.Content = JsonContent.Create(payload);
        (await http.SendAsync(r, ct)).EnsureSuccessStatusCode();
    }
}

public sealed class LiveActivityPushJob(NodusDbContext db, ILiveActivityPushClient push)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(Guid scheduleId)
    {
        var s = await db.LiveActivityPushSchedules
            .Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.Id == scheduleId);
        
        if (s is null || s.Status != PushScheduleStatus.Scheduled) return;
        try
        {
            if (string.IsNullOrWhiteSpace(s.Device.LiveActivityPushToken))
                throw new InvalidOperationException("Device has no Live Activity push token.");
            using var doc = JsonDocument.Parse(s.PayloadJson);
            await push.SendAsync(s.Device.LiveActivityPushToken, s.ActivityId, doc.RootElement, CancellationToken.None);
            s.Status = PushScheduleStatus.Sent;
            s.SentAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            s.Status = PushScheduleStatus.Failed;
            s.Error = ex.Message;
            throw;
        }
        finally
        {
            await db.SaveChangesAsync();
        }
    }
}

public record SubscriptionRestoreResult(
    bool Success,
    string? Message = null,
    string? PlanCode = null,
    DateTimeOffset? ExpiresAt = null);

public interface ISubscriptionRestorationService
{
    Task SendCodeAsync(string email, CancellationToken ct);
    Task<SubscriptionRestoreResult> RestoreAsync(ConfirmSubscriptionRestoreRequest request, CancellationToken ct);
}

public sealed class SubscriptionRestorationService(
    NodusDbContext db,
    Resend.IResend resend,
    IOptions<ResendOptions> options) : ISubscriptionRestorationService
{
    public async Task SendCodeAsync(string rawEmail, CancellationToken ct)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var o = options.Value;
        // Do not create or send a code for an email without an active subscription.
        if (!await db.Subscriptions.AnyAsync(s => s.Email == email && s.ExpiresAt > now, ct)) return;
        if (await db.SubscriptionRestoreCodes.AnyAsync(
                c => c.Email == email && c.CreatedAt > now.AddSeconds(-o.ResendCooldownSeconds), ct)) return;
        if (await db.SubscriptionRestoreCodes.CountAsync(c => c.Email == email && c.CreatedAt > now.AddHours(-1), ct) >=
            o.MaxCodesPerHour) return;
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        db.SubscriptionRestoreCodes.Add(new SubscriptionRestoreCode
        {
            Email = email, CodeHash = Hash(email, code, o.HashPepper), CreatedAt = now,
            ExpiresAt = now.AddMinutes(o.CodeExpiryMinutes)
        });
        var message = new Resend.EmailMessage
        {
            From = o.From, Subject = "Nodus: восстановление подписки",
            HtmlBody =
                $"<p>Ваш код для восстановления подписки: <strong>{code}</strong></p><p>Код действует {o.CodeExpiryMinutes} минут.</p>"
        };
        message.To.Add(email);
        var response = await resend.EmailSendAsync(message, ct);
        if (!response.Success)
            throw new InvalidOperationException(response.Exception?.Message ?? "Unable to send email through Resend.");
        await db.SaveChangesAsync(ct);
    }

    public async Task<SubscriptionRestoreResult> RestoreAsync(ConfirmSubscriptionRestoreRequest request,
        CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var o = options.Value;
        var code = await db.SubscriptionRestoreCodes.OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(c => c.Email == email && c.UsedAt == null && c.ExpiresAt > now, ct);
        if (code is null) return new(false, "Code was not found or has expired.");
        if (++code.Attempts > 5)
        {
            code.UsedAt = now;
            await db.SaveChangesAsync(ct);
            return new(false, "Too many incorrect attempts.");
        }

        var expected = Convert.FromBase64String(code.CodeHash);
        var actual = Convert.FromBase64String(Hash(email, request.Code, o.HashPepper));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await db.SaveChangesAsync(ct);
            return new(false, "Incorrect verification code.");
        }

        code.UsedAt = now;
        var subscriptions = await db.Subscriptions.Where(s => s.Email == email && s.ExpiresAt > now).ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return new(false, "No active subscription was found.");
        }

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Email == email && d.DeviceId == request.DeviceId, ct);
        if (device is null)
        {
            device = new Device
            {
                Email = email, DeviceId = request.DeviceId, LiveActivityPushToken = request.LiveActivityPushToken,
                UpdatedAt = now
            };
            db.Devices.Add(device);
        }
        else
        {
            device.LiveActivityPushToken ??= request.LiveActivityPushToken;
            device.UpdatedAt = now;
        }

        foreach (var subscription in subscriptions) subscription.DeviceId = request.DeviceId;
        await db.SaveChangesAsync(ct);
        var primary = subscriptions.OrderByDescending(s => s.ExpiresAt).First();
        return new(true, PlanCode: primary.PlanCode, ExpiresAt: primary.ExpiresAt);
    }

    private static string Hash(string email, string code, string pepper) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{email}:{code}:{pepper}")));
}

public record SubscriptionPlan(string Code, decimal Amount, string Currency, int DurationDays);

public static class SubscriptionPlans
{
    private static readonly SubscriptionPlan[] Plans =
        [new("monthly", 299m, "RUB", 30), new("yearly", 2490m, "RUB", 365)];

    public static SubscriptionPlan? Find(string code) =>
        Plans.SingleOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

public record ProviderPayment(string Id, string? ConfirmationUrl, PaymentStatus Status);

public interface IYooKassaClient
{
    Task<ProviderPayment> CreateAsync(Payment p, string returnUrl, CancellationToken ct);
    Task<ProviderPayment> GetAsync(string id, CancellationToken ct);
}

public sealed class YooKassaClient(HttpClient http, IOptions<YooKassaOptions> options) : IYooKassaClient
{
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ShopId) || string.IsNullOrWhiteSpace(o.SecretKey))
            throw new InvalidOperationException("YooKassa credentials are not configured.");
        var r = new HttpRequestMessage(method, new Uri(new Uri(o.BaseUrl), path));
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.ShopId}:{o.SecretKey}")));
        return r;
    }

    public async Task<ProviderPayment> CreateAsync(Payment p, string returnUrl, CancellationToken ct)
    {
        using var r = Request(HttpMethod.Post, "payments");
        r.Headers.Add("Idempotence-Key", p.Id.ToString());
        r.Content = JsonContent.Create(new
        {
            amount = new
            {
                value = p.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                currency = p.Currency
            },
            confirmation = new { type = "redirect", return_url = returnUrl }, capture = true,
            description = $"Nodus {p.PlanCode} subscription",
            metadata = new { payment_id = p.Id, email = p.Email, device_id = p.DeviceDbId }
        });
        return await Parse(await http.SendAsync(r, ct), ct);
    }

    public async Task<ProviderPayment> GetAsync(string id, CancellationToken ct)
    {
        using var r = Request(HttpMethod.Get, $"payments/{id}");
        return await Parse(await http.SendAsync(r, ct), ct);
    }

    private static async Task<ProviderPayment> Parse(HttpResponseMessage r, CancellationToken ct)
    {
        r.EnsureSuccessStatusCode();
        using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var root = j.RootElement;
        return new(root.GetProperty("id").GetString()!,
            root.TryGetProperty("confirmation", out var c) && c.TryGetProperty("confirmation_url", out var u)
                ? u.GetString()
                : null,
            root.GetProperty("status").GetString() switch
            {
                "succeeded" => PaymentStatus.Succeeded, "waiting_for_capture" => PaymentStatus.WaitingForCapture,
                "canceled" => PaymentStatus.Canceled, _ => PaymentStatus.Pending
            });
    }
}