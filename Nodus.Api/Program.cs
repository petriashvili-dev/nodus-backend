using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nodus.Api;
using Resend;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<YooKassaOptions>(builder.Configuration.GetSection("YooKassa"));
builder.Services.Configure<LiveActivityPushOptions>(builder.Configuration.GetSection("LiveActivityPush"));
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection("Resend"));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
builder.Services.AddDbContext<NodusDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddHangfire(o => o.UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();
builder.Services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>();
builder.Services.AddHttpClient<IYooKassaClient, YooKassaClient>();
builder.Services.AddHttpClient<ILiveActivityPushClient, LiveActivityPushClient>();
builder.Services.AddHttpClient<IResend, ResendClient>();
builder.Services.Configure<ResendClientOptions>(o => o.ApiToken = builder.Configuration["Resend:ApiKey"] ?? "");
builder.Services.AddScoped<ISubscriptionRestorationService, SubscriptionRestorationService>();
builder.Services.AddScoped<LiveActivityPushJob>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<NodusDbContext>().Database.MigrateAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/llm/completions", async (LlmCompletionRequest request, ILlmClient llm, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["prompt"] = ["Prompt is required."] });
    var result = await llm.CompleteAsync(request, ct);
    return Results.Json(result);
});

app.MapPost("/api/live-activities/schedule", async (ScheduleLiveActivityRequest request, NodusDbContext db,
    IBackgroundJobClient jobs, CancellationToken ct) =>
{
    if (request.StartsAt <= DateTimeOffset.UtcNow.AddMinutes(30))
        return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["startsAt"] = ["The activity must start more than 30 minutes from now."] });

    var device =
        await db.Devices.SingleOrDefaultAsync(
            d => d.Email == request.Email.ToLowerInvariant() && d.DeviceId == request.DeviceId, ct);
    if (device is null)
    {
        device = new Device
        {
            Email = request.Email.ToLowerInvariant(), DeviceId = request.DeviceId,
            LiveActivityPushToken = request.LiveActivityPushToken, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Devices.Add(device);
    }
    else
    {
        device.LiveActivityPushToken = request.LiveActivityPushToken;
        device.UpdatedAt = DateTimeOffset.UtcNow;
    }

    var metadata = new LiveActivityPushSchedule
    {
        Device = device, ActivityId = request.ActivityId, StartsAt = request.StartsAt,
        PushAt = request.StartsAt.AddMinutes(-30),
        PayloadJson = request.Payload.ValueKind is JsonValueKind.Undefined ? "{}" : request.Payload.GetRawText(),
        Status = PushScheduleStatus.Scheduled, CreatedAt = DateTimeOffset.UtcNow
    };
    db.LiveActivityPushSchedules.Add(metadata);
    await db.SaveChangesAsync(ct);
    metadata.HangfireJobId = jobs.Schedule<LiveActivityPushJob>(j => j.ExecuteAsync(metadata.Id), metadata.PushAt);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/live-activities/schedule/{metadata.Id}",
        new { metadata.Id, metadata.PushAt, metadata.HangfireJobId });
});

app.MapGet("/api/live-activities/schedule/{id:guid}", async (Guid id, NodusDbContext db, CancellationToken ct) =>
    await db.LiveActivityPushSchedules.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is { } s
        ? Results.Ok(new { s.Id, s.ActivityId, s.StartsAt, s.PushAt, s.Status, s.SentAt, s.Error })
        : Results.NotFound());

app.MapPost("/api/payments/subscription", async (CreateSubscriptionPaymentRequest request, NodusDbContext db,
    IYooKassaClient yooKassa, CancellationToken ct) =>
{
    var plan = SubscriptionPlans.Find(request.PlanCode);
    if (plan is null)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["planCode"] = ["Unknown plan code."] });
    var email = request.Email.ToLowerInvariant();
    var device = await db.Devices.SingleOrDefaultAsync(d => d.Email == email && d.DeviceId == request.DeviceId, ct);
    if (device is null)
    {
        device = new Device { Email = email, DeviceId = request.DeviceId, UpdatedAt = DateTimeOffset.UtcNow };
        db.Devices.Add(device);
    }

    var payment = new Payment
    {
        Email = email, Device = device, PlanCode = plan.Code, Amount = plan.Amount, Currency = plan.Currency,
        DurationDays = plan.DurationDays, Status = PaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow
    };
    db.Payments.Add(payment);
    await db.SaveChangesAsync(ct);
    var providerPayment = await yooKassa.CreateAsync(payment, request.ReturnUrl, ct);
    payment.ProviderPaymentId = providerPayment.Id;
    payment.ConfirmationUrl = providerPayment.ConfirmationUrl;
    payment.Status = providerPayment.Status;
    payment.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/payments/{payment.Id}",
        new { payment.Id, payment.Status, payment.ConfirmationUrl, payment.Amount, payment.Currency });
});

app.MapPost("/api/payments/yookassa/webhook",
    async (YooKassaWebhook webhook, NodusDbContext db, IYooKassaClient yooKassa, CancellationToken ct) =>
    {
        var payment = await db.Payments.Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.ProviderPaymentId == webhook.Object.Id, ct);
        if (payment is null) return Results.NotFound();
        var provider = await yooKassa.GetAsync(payment.ProviderPaymentId!, ct);
        payment.Status = provider.Status;
        payment.UpdatedAt = DateTimeOffset.UtcNow;
        if (provider.Status == PaymentStatus.Succeeded &&
            !await db.Subscriptions.AnyAsync(s => s.PaymentId == payment.Id, ct))
        {
            var current = await db.Subscriptions.SingleOrDefaultAsync(
                s => s.Email == payment.Email && s.DeviceId == payment.Device.DeviceId &&
                     s.ExpiresAt > DateTimeOffset.UtcNow, ct);
            var begins = current?.ExpiresAt ?? DateTimeOffset.UtcNow;
            if (current is null)
                db.Subscriptions.Add(new Subscription
                {
                    Email = payment.Email, DeviceId = payment.Device.DeviceId, PlanCode = payment.PlanCode,
                    StartsAt = begins, ExpiresAt = begins.AddDays(payment.DurationDays), Payment = payment
                });
            else
            {
                current.ExpiresAt = begins.AddDays(payment.DurationDays);
                current.Payment = payment;
            }

            payment.PaidAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok();
    });

app.MapGet("/api/subscriptions/{email}/{deviceId}",
    async (string email, string deviceId, NodusDbContext db, CancellationToken ct) =>
        await db.Subscriptions.AsNoTracking().Where(s => s.Email == email.ToLowerInvariant() && s.DeviceId == deviceId)
            .OrderByDescending(s => s.ExpiresAt).FirstOrDefaultAsync(ct) is { } s
            ? Results.Ok(new { s.PlanCode, s.StartsAt, s.ExpiresAt, isActive = s.ExpiresAt > DateTimeOffset.UtcNow })
            : Results.NotFound());

app.MapPost("/api/subscriptions/restore/request", async (SubscriptionRestoreRequest request,
    ISubscriptionRestorationService restoration, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Email is required."] });
    await restoration.SendCodeAsync(request.Email, ct);
    // The same response is returned for every email to avoid revealing subscription ownership.
    return Results.Accepted(value: new
        { message = "If an active subscription exists, a verification code has been sent." });
});

app.MapPost("/api/subscriptions/restore/confirm", async (ConfirmSubscriptionRestoreRequest request,
    ISubscriptionRestorationService restoration, CancellationToken ct) =>
{
    var result = await restoration.RestoreAsync(request, ct);
    return result.Success
        ? Results.Ok(new { result.PlanCode, result.ExpiresAt })
        : Results.BadRequest(new { message = result.Message });
});

app.Run();