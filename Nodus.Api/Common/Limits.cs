namespace Nodus.Api.Common;

public static class Limits
{
    public const int TitleLength = 200;
    public const int DescriptionLength = 4000;
    public const int BatchSize = 50;
    public const int IdempotencyKeyLength = 128;
    public const long RequestBodyBytes = 256 * 1024;
}
