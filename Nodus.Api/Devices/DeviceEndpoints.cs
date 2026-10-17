using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.Devices;

public sealed record RegisterDeviceRequest(
    string? InstallationId,
    string? ApnsToken,
    ApnsEnvironment? ApnsEnvironment,
    string? AppVersion);

public sealed record DeviceDto(
    Guid Id,
    string InstallationId,
    string Platform,
    bool HasPushToken,
    ApnsEnvironment ApnsEnvironment,
    string? AppVersion,
    DateTimeOffset LastSeenAt)
{
    public static DeviceDto From(Device d) =>
        new(d.Id, d.InstallationId, d.Platform, d.ApnsToken is not null, d.ApnsEnvironment, d.AppVersion, d.LastSeenAt);
}

public static partial class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this RouteGroupBuilder v1)
    {
        var devices = v1.MapGroup("/devices").WithTags("Devices");
        devices.MapPost("/", RegisterAsync);
        devices.MapDelete("/{installationId}", UnregisterAsync);
    }

    /// <summary>
    /// Upsert by installation; naturally idempotent, so no Idempotency-Key is required.
    /// A token can belong to one installation only: when the app signs in to another account on the same phone,
    /// the token moves and the previous account stops receiving pushes there.
    /// </summary>
    private static async Task<ApiResult> RegisterAsync(RegisterDeviceRequest request, HttpContext http, NodusDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var token = request.ApnsToken?.Trim().ToLowerInvariant();
        var errors = new ValidationErrors()
            .AddIf(string.IsNullOrWhiteSpace(request.InstallationId) || request.InstallationId.Length > 64,
                "installation_id", "Installation id (1–64 characters) is required.")
            .AddIf(token is not null && !TokenPattern().IsMatch(token), "apns_token", "APNs token must be a hex string.")
            .AddIf(request.AppVersion?.Length > 32, "app_version", "App version is too long.");
        if (errors.HasErrors) return errors.ToResult();

        var userId = http.User.GetUserId();
        var now = time.GetUtcNow();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.UserId == userId && d.InstallationId == request.InstallationId, ct);
        if (device is null)
        {
            device = new Device { Id = Guid.NewGuid(), UserId = userId, InstallationId = request.InstallationId!, CreatedAt = now };
            db.Devices.Add(device);
        }

        if (token is not null)
        {
            await db.Devices.Where(d => d.ApnsToken == token && d.Id != device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ApnsToken, (string?)null), ct);
            device.ApnsToken = token;
            device.TokenInvalidatedAt = null;
        }

        device.ApnsEnvironment = request.ApnsEnvironment ?? device.ApnsEnvironment;
        device.AppVersion = request.AppVersion ?? device.AppVersion;
        device.LastSeenAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return ApiResult.Problem(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                "The device was registered concurrently; retry.");
        }

        return ApiResult.Ok(DeviceDto.From(device));
    }

    /// <summary>Called on logout so the installation stops receiving this account's pushes.</summary>
    private static async Task<ApiResult> UnregisterAsync(string installationId, HttpContext http, NodusDbContext db,
        CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        await db.Devices.Where(d => d.UserId == userId && d.InstallationId == installationId).ExecuteDeleteAsync(ct);
        return ApiResult.NoContent();
    }

    [GeneratedRegex("^[0-9a-f]{32,200}$")]
    private static partial Regex TokenPattern();
}
