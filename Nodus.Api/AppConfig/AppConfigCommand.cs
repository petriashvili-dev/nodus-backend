using System.Text;
using System.Text.Json;

namespace Nodus.Api.AppConfig;

/// <summary>
/// <c>config list | get KEY | set KEY VALUE [--json] | delete KEY</c>: edits client settings in the database while the
/// API keeps running. Without <c>--json</c> the value is stored as a string. Returns the process exit code.
/// </summary>
public static class AppConfigCommand
{
    private const string Usage = """
        Usage:
          config list
          config get <key>
          config set <key> <value>           (stored as a string)
          config set <key> <json> --json     (number, boolean, array, object)
          config delete <key>
        Keys: lower-case snake_case segments separated by dots, e.g. links.privacy_policy
        """;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<AppConfigService>();
        switch (args)
        {
            case ["list"]:
                var entries = await config.ListAsync(ct);
                foreach (var entry in entries) await output.WriteLineAsync($"{entry.Key}\t{entry.Value}\t{entry.UpdatedAt:O}");
                await output.WriteLineAsync($"{entries.Count} key(s).");
                return 0;
            case ["get", var key]:
                var found = (await config.ListAsync(ct)).SingleOrDefault(e => e.Key == key);
                await output.WriteLineAsync(found?.Value ?? "Key not found.");
                return found is null ? 1 : 0;
            case ["set", var key, var raw, .. var flags] when flags is [] or ["--json"]:
                if (!AppConfigService.IsValidKey(key)) return await FailAsync(output, "Invalid key.");
                if (!TryParseValue(raw, flags is ["--json"], out var value)) return await FailAsync(output, "Invalid JSON value.");
                if (Encoding.UTF8.GetByteCount(value.GetRawText()) > AppConfigService.MaxValueBytes)
                    return await FailAsync(output, $"The value must be at most {AppConfigService.MaxValueBytes} bytes.");
                var change = await config.SetAsync(key, value, ct);
                await output.WriteLineAsync(change == AppConfigChange.Changed ? "Value set." : "Value is already set.");
                return 0;
            case ["delete", var key]:
                var deleted = await config.DeleteAsync(key, ct) == AppConfigChange.Changed;
                await output.WriteLineAsync(deleted ? "Key deleted." : "Key not found.");
                return deleted ? 0 : 1;
            default:
                return await FailAsync(output, "Unknown command.");
        }
    }

    private static bool TryParseValue(string raw, bool json, out JsonElement value)
    {
        if (!json)
        {
            value = JsonSerializer.SerializeToElement(raw);
            return true;
        }

        try
        {
            value = JsonDocument.Parse(raw).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static async Task<int> FailAsync(TextWriter output, string error)
    {
        await output.WriteLineAsync($"{error}\n{Usage}");
        return 2;
    }
}
