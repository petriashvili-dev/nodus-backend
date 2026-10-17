namespace Nodus.Api.Common;

public static class Limits
{
    public const int TitleLength = 200;
    public const int DescriptionLength = 4000;
    public const int BatchSize = 50;
    public const int IdempotencyKeyLength = 128;
    public const long RequestBodyBytes = 256 * 1024;

    /// <summary>Import uploads only (a 200k-character file in Cyrillic UTF-8 is up to ~400 KB).</summary>
    public const long ImportBodyBytes = 1024 * 1024;
}
