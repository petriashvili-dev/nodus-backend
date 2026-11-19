using MimeKit;

namespace Nodus.Api.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "EmailOptions";
    public string FromAddress { get; init; } = "no-reply@nodus.app";
    public string FromName { get; init; } = "Nodus";
    public SmtpOptions Smtp { get; init; } = new();

    public bool HasValidSender() => !string.IsNullOrWhiteSpace(FromAddress) && MailboxAddress.TryParse(FromAddress, out var address)
        && !string.IsNullOrWhiteSpace(address.Domain) && address.Address == FromAddress
        && !FromAddress.Contains('\r') && !FromAddress.Contains('\n')
        && FromName is not null && !FromName.Contains('\r') && !FromName.Contains('\n');
}

public sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 25;
    public string Security { get; init; } = "None";
    public bool Authentication { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 15;

    public bool IsValid() => !string.IsNullOrWhiteSpace(Host) && !Host.Contains("://")
        && !Host.Any(char.IsWhiteSpace) && Port is > 0 and <= 65535
        && Security is "None" or "StartTls" or "SslOnConnect"
        && TimeoutSeconds is > 0 and <= 300
        && (!Authentication || (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password)));
}
