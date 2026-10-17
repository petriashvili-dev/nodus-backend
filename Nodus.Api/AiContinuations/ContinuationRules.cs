using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nodus.Api.AiContinuations;

public enum ContinuationDecisionKind
{
    Skip,
    Template,
    NeedsModel
}

public sealed record ContinuationDecision(ContinuationDecisionKind Kind, string? Title = null, string? Reason = null)
{
    public static ContinuationDecision Skip(string reason) => new(ContinuationDecisionKind.Skip, Reason: reason);
}

/// <summary>
/// The cheap, deterministic part of the pipeline: semantic rules and generalised templates. Conservative by design:
/// without an explicit sign of a preparatory action (signing up, booking, buying tickets…) the answer is Skip
/// and no model is called.
/// </summary>
public static partial class ContinuationRules
{
    public const string TemplatesVersion = "continuation-templates-v1";

    private static readonly (Regex Pattern, Func<Match, string> Continuation)[] Templates =
    [
        (Pattern(@"^записаться к (?<who>врачу|доктору|стоматологу|терапевту|окулисту|офтальмологу|гинекологу|урологу|дерматологу|кардиологу|неврологу|педиатру|лору|ортопеду|психологу|парикмахеру|мастеру|ветеринару)$"),
            m => $"Визит к {m.Groups["who"].Value}"),
        (Pattern(@"^записаться на (?<what>приём|прием|стрижку|маникюр|педикюр|массаж|мрт|кт|узи|флюорографию|анализы|прививку|вакцинацию|диспансеризацию|техосмотр|то|шиномонтаж|собеседование|консультацию)$"),
            m => $"Сходить на {m.Groups["what"].Value}"),
        (Pattern(@"^купить билеты? (?<prep>на|в) (?<what>концерт|спектакль|театр|кино|фильм|матч|выставку|музей|цирк|балет|оперу)$"),
            m => $"Сходить {m.Groups["prep"].Value} {m.Groups["what"].Value}"),
        (Pattern(@"^забронировать (?<what>отель|гостиницу|жильё|жилье|квартиру)$"),
            _ => "Заселиться"),
        (Pattern(@"^забронировать (?<what>столик|стол)$"),
            _ => "Сходить в ресторан"),
        (Pattern(@"^назначить (?<what>встречу|собеседование|созвон)$"),
            m => m.Groups["what"].Value == "созвон" ? "Созвониться" : $"Провести {m.Groups["what"].Value}")
    ];

    /// <summary>Openings that make a follow-up step plausible. Anything else is skipped.</summary>
    private static readonly string[] PreparatoryOpenings =
    [
        "записаться", "забронировать", "купить билет", "заказать", "подать заявк", "подать документ", "отправить заявк",
        "отправить резюме", "отправить документ", "назначить", "договориться о", "оформить", "зарегистрироваться на",
        "оплатить курс", "оплатить поездк", "запланировать"
    ];

    public static ContinuationDecision Evaluate(string title)
    {
        var normalized = Normalize(title);
        if (normalized.Length < 6) return ContinuationDecision.Skip("too_short");
        if (!PreparatoryOpenings.Any(normalized.StartsWith)) return ContinuationDecision.Skip("no_signal");

        foreach (var (pattern, continuation) in Templates)
            if (pattern.Match(normalized) is { Success: true } match)
                return new ContinuationDecision(ContinuationDecisionKind.Template, Capitalize(continuation(match)));

        return new ContinuationDecision(ContinuationDecisionKind.NeedsModel);
    }

    /// <summary>Lower case, ё→е, punctuation removed, whitespace collapsed.</summary>
    public static string Normalize(string text) =>
        Whitespace().Replace(Punctuation().Replace(text.ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim();

    /// <summary>Semantic content only: dates and times do not change it, so rescheduling never re-runs the pipeline.</summary>
    public static string ContentHash(string title, string? description) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Normalize(title)}\n{Normalize(description ?? "")}")));

    /// <summary>
    /// Heuristic guard for the shared cache: no digits, no capitalised words after the first (names, places),
    /// at most eight words, and no description (free text is personal by nature).
    /// </summary>
    public static bool IsCacheable(string title, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description)) return false;
        var words = title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 8 && !title.Any(char.IsDigit) && words.Skip(1).All(w => !char.IsUpper(w[0]));
    }

    public static string CacheKey(string normalizedTitle, string promptVersion, string pepper) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper),
            Encoding.UTF8.GetBytes($"{promptVersion}:{normalizedTitle}")));

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
