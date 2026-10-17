using Nodus.Api.AiContinuations;

namespace Nodus.Tests.Ai;

public class ContinuationRulesTests
{
    [Theory]
    [InlineData("Записаться к врачу", "Визит к врачу")]
    [InlineData("записаться к стоматологу!", "Визит к стоматологу")]
    [InlineData("Записаться на МРТ", "Сходить на мрт")]
    [InlineData("Купить билеты в театр", "Сходить в театр")]
    [InlineData("Купить билет на концерт", "Сходить на концерт")]
    [InlineData("Назначить созвон", "Созвониться")]
    public void Templates_answer_without_a_model(string title, string expected)
    {
        var decision = ContinuationRules.Evaluate(title);

        Assert.Equal((ContinuationDecisionKind.Template, expected), (decision.Kind, decision.Title));
    }

    [Theory]
    [InlineData("Купить хлеб")]
    [InlineData("Позвонить маме")]
    [InlineData("Зарядка")]
    [InlineData("Бег")]
    public void Without_a_preparatory_signal_the_answer_is_skip(string title) =>
        Assert.Equal(ContinuationDecisionKind.Skip, ContinuationRules.Evaluate(title).Kind);

    [Theory]
    [InlineData("Подать документы на визу")]
    [InlineData("Заказать торт")]
    [InlineData("Записаться в автошколу")]
    public void Plausible_but_unknown_cases_go_to_the_model(string title) =>
        Assert.Equal(ContinuationDecisionKind.NeedsModel, ContinuationRules.Evaluate(title).Kind);

    [Fact]
    public void Content_hash_ignores_case_punctuation_and_yo()
    {
        Assert.Equal(ContinuationRules.ContentHash("Записаться к врачу!", null), ContinuationRules.ContentHash("записаться  к врачу", ""));
        Assert.NotEqual(ContinuationRules.ContentHash("Записаться к врачу", null), ContinuationRules.ContentHash("Записаться к врачу", "кардиолог"));
        Assert.Equal(ContinuationRules.Normalize("Ёлка"), ContinuationRules.Normalize("елка"));
    }

    [Theory]
    [InlineData("Заказать торт", null, true)]
    [InlineData("Заказать торт для Маши", null, false)]
    [InlineData("Заказать 3 торта", null, false)]
    [InlineData("Заказать торт", "шоколадный", false)]
    public void Only_titles_without_signs_of_personal_data_are_cacheable(string title, string? description, bool expected) =>
        Assert.Equal(expected, ContinuationRules.IsCacheable(title, description));

    [Theory]
    [InlineData("""{"continuation":{"title":"Получить визу"}}""", "Получить визу")]
    [InlineData("""{"continuation":null}""", null)]
    [InlineData("""{"continuation":{"title":"подать документы на визу"}}""", null)]
    [InlineData("garbage", null)]
    public void Model_output_is_parsed_conservatively(string content, string? expected) =>
        Assert.Equal(expected, ContinuationRunner.ParseTitle(content, "подать документы на визу"));
}
