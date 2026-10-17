namespace Nodus.Api.Devices;

/// <summary>
/// APNs gateway a token belongs to. Development builds receive sandbox tokens, TestFlight/App Store builds
/// production tokens; a token sent to the wrong gateway is rejected as <c>BadDeviceToken</c>.
/// </summary>
public enum ApnsEnvironment
{
    Unknown,
    Sandbox,
    Production
}

/// <summary>One app installation of a user. Push tokens are stored here, separately from the account.</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Client-generated id of the installation (kept in the Keychain), stable across token changes.</summary>
    public required string InstallationId { get; set; }

    public string Platform { get; set; } = "ios";
    public string? ApnsToken { get; set; }
    public ApnsEnvironment ApnsEnvironment { get; set; }
    public string? AppVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Set when APNs reported the token as invalid; the token itself is cleared.</summary>
    public DateTimeOffset? TokenInvalidatedAt { get; set; }
}
