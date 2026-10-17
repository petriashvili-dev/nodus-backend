using Nodus.Api.AiDrafts;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Nodes;
using Nodus.Api.Recurrence;

namespace Nodus.Tests.Ai;

public class NodeDraftParserTests
{
    private static readonly DateTimeOffset Reference = DateTimeOffset.Parse("2026-10-17T10:00:00+03:00");

    private static IReadOnlyList<NodeDraftDto> Parse(string json) =>
        NodeDraftParser.Parse(json, "Europe/Moscow", Reference, "test");

    [Fact]
    public void Several_nodes_of_different_types_are_parsed()
    {
        var drafts = Parse("""
            {"nodes":[
              {"type":"one_time","title":"Купить молоко","description":null,"date":"2026-10-18","time":"9:30","recurrence":null,"track_years":null,"ambiguities":[]},
              {"type":"recurring","title":"Зарядка","description":null,"date":null,"time":null,
               "recurrence":{"frequency":"weekly","interval":1,"start_date":null,"until":null,"times":["07:00"],"week_days":["monday","friday"],"month_day":null},
               "track_years":null,"ambiguities":[]},
              {"type":"anniversary","title":"ДР мамы","description":null,"date":"1965-03-08","time":null,"recurrence":null,"track_years":true,"ambiguities":[]}
            ]}
            """);

        Assert.Equal([NodeType.OneTime, NodeType.Recurring, NodeType.Anniversary], drafts.Select(d => d.Type));
        Assert.Equal((new DateOnly(2026, 10, 18), new TimeOnly(9, 30)), (drafts[0].Date!.Value, drafts[0].Time!.Value));
        var rule = drafts[1].Recurrence!;
        Assert.Equal((RecurrenceFrequency.Weekly, new DateOnly(2026, 10, 17)), (rule.Frequency, rule.StartDate));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], rule.WeekDays);
        Assert.True(drafts[2].TrackYears);
        Assert.All(drafts, d => Assert.Empty(d.Issues));
    }

    [Fact]
    public void Missing_or_broken_facts_become_issues_instead_of_guesses()
    {
        var drafts = Parse("""
            {"nodes":[
              {"type":"one_time","title":"Позвонить врачу","date":null,"time":"10:00","ambiguities":["Не ясно, какой день"]},
              {"type":"recurring","title":"Полив цветов","recurrence":{"frequency":"sometimes"}},
              {"type":"one_time","title":"Отчёт","date":"2026-13-45"},
              {"type":"one_time","title":"Вчерашнее","date":"2026-10-10"},
              {"type":"anniversary","title":"Годовщина"},
              {"type":"one_time","title":"   "}
            ]}
            """);

        Assert.Equal(5, drafts.Count);
        Assert.Null(drafts[0].Time);
        Assert.Equal(["time_without_date", "ambiguous"], drafts[0].Issues.Select(i => i.Code));
        Assert.Equal((NodeType.OneTime, "missing_recurrence"), (drafts[1].Type, drafts[1].Issues[0].Code));
        Assert.Contains(drafts[2].Issues, i => i.Code == "invalid_date");
        Assert.Contains(drafts[3].Issues, i => i.Code == "past_date");
        Assert.Contains(drafts[4].Issues, i => i.Code == "missing_date");
        Assert.Contains(drafts[4].Issues, i => i.Code == "needs_review");
    }

    [Fact]
    public void Null_numbers_in_a_rule_fall_back_to_defaults()
    {
        var draft = Assert.Single(Parse("""
            {"nodes":[{"type":"recurring","title":"Оплатить аренду",
              "recurrence":{"frequency":"monthly","interval":null,"start_date":"2026-10-05","until":null,"times":[],"week_days":[],"month_day":null}}]}
            """));

        Assert.Equal((1, (int?)null), (draft.Recurrence!.Interval, draft.Recurrence.MonthDay));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"items":[]}""")]
    public void Unexpected_structure_is_an_invalid_response(string content)
    {
        var error = Assert.Throws<LlmException>(() => Parse(content));

        Assert.Equal(LlmErrorCategory.InvalidResponse, error.Category);
    }

    [Fact]
    public void Prompt_resolves_relative_dates_against_the_dictation_moment()
    {
        var request = NodeDraftParser.BuildRequest("завтра купить хлеб", "Europe/Moscow", Reference);

        Assert.Contains("2026-10-17 10:00, суббота", request.Messages[0].Content);
        Assert.Equal("завтра купить хлеб", request.Messages[1].Content);
        Assert.NotNull(request.Schema);
    }
}
