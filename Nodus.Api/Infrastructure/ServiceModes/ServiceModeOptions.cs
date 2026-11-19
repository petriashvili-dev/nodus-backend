namespace Nodus.Api.Infrastructure.ServiceModes;

/// <summary>Server-only switches; never part of the public client configuration.</summary>
public sealed class ServiceModeOptions
{
    public const string Section = "ServiceMode";
    public EmailServiceMode Email { get; init; } = EmailServiceMode.Live;
}

public enum EmailServiceMode
{
    Live,
    Mock
}
