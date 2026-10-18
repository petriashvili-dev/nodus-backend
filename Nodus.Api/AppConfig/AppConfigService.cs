using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nodus.Api.AiDrafts;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.DataPortability;
using Nodus.Api.Infrastructure.Persistence;

namespace Nodus.Api.AppConfig;

/// <summary>A client setting (link, version, switch) changed at runtime with the <c>config</c> command.</summary>
public sealed class AppConfigEntry
{
    public string Key { get; set; } = "";

    /// <summary>Any JSON value: string, number, boolean, array or object.</summary>
    public string Value { get; set; } = "null";

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppConfigOptions
{
    public const string Section = "AppConfig";

    /// <summary>How long the API serves a built response; changes made by the command appear within this time.</summary>
    public int CacheSeconds { get; init; } = 15;
}

/// <summary>The serialised response and its strong validator.</summary>
public sealed record AppConfigDocument(string Json, string ETag);

public enum AppConfigChange
{
    Changed,
    Unchanged,
    NotFound
}

public sealed partial class AppConfigService(
    NodusDbContext db,
    IMemoryCache cache,
    IOptions<AppConfigOptions> options,
    IOptions<AiOptions> ai,
    IOptions<ImportOptions> import,
    TimeProvider time,
    ILogger<AppConfigService> logger)
{
    public const int MaxKeyLength = 100;
    public const int MaxValueBytes = 8 * 1024;
    private const string CacheKey = "app-config";

    /// <summary>Lower-case snake_case segments separated by dots: <c>links.privacy_policy</c>.</summary>
    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= MaxKeyLength } && KeyPattern().IsMatch(key);

    public async Task<AppConfigDocument> GetDocumentAsync(CancellationToken ct)
    {
        var seconds = options.Value.CacheSeconds;
        if (seconds <= 0) return await BuildAsync(ct);
        return (await cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
            return BuildAsync(ct);
        }))!;
    }

    public Task<List<AppConfigEntry>> ListAsync(CancellationToken ct) =>
        db.AppConfig.AsNoTracking().OrderBy(e => e.Key).ToListAsync(ct);

    public async Task<AppConfigChange> SetAsync(string key, JsonElement value, CancellationToken ct)
    {
        var json = value.GetRawText();
        var entry = await db.AppConfig.SingleOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null) db.AppConfig.Add(entry = new AppConfigEntry { Key = key });
        else if (JsonElement.DeepEquals(JsonDocument.Parse(entry.Value).RootElement, value)) return AppConfigChange.Unchanged;

        entry.Value = json;
        entry.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        logger.LogInformation("App config {Key} set", key);
        return AppConfigChange.Changed;
    }

    public async Task<AppConfigChange> DeleteAsync(string key, CancellationToken ct)
    {
        if (await db.AppConfig.Where(e => e.Key == key).ExecuteDeleteAsync(ct) == 0) return AppConfigChange.NotFound;
        cache.Remove(CacheKey);
        logger.LogInformation("App config {Key} deleted", key);
        return AppConfigChange.Changed;
    }

    /// <summary>
    /// <c>values</c> come from the table; <c>limits</c> are read-only copies of the server's own settings, so the
    /// client checks input with exactly the numbers the server enforces.
    /// </summary>
    private async Task<AppConfigDocument> BuildAsync(CancellationToken ct)
    {
        var entries = await ListAsync(ct);
        var values = new JsonObject();
        foreach (var entry in entries) values[entry.Key] = JsonNode.Parse(entry.Value);

        var body = new JsonObject
        {
            ["values"] = values,
            ["limits"] = new JsonObject
            {
                ["ai_draft_text_max_chars"] = ai.Value.MaxTextLength,
                ["import_max_chars"] = import.Value.MaxContentChars,
                ["import_ai_max_chars"] = import.Value.MaxAiInputChars,
                ["display_name_max_chars"] = UserProfile.DisplayNameLength
            },
            ["updated_at"] = entries.Count == 0 ? null : entries.Max(e => e.UpdatedAt)
        };
        var json = body.ToJsonString(NodusJson.Options);
        return new AppConfigDocument(json, $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32]}\"");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex KeyPattern();
}
