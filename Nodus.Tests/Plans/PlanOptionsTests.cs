using Microsoft.Extensions.Configuration;
using Nodus.Api.Plans;

namespace Nodus.Tests.Plans;

public class PlanOptionsTests
{
    private static PlanOptions Bind(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(PlanOptions.Section).Get<PlanOptions>()
        ?? new PlanOptions();

    [Fact]
    public void Defaults_give_plus_every_feature_and_standard_none()
    {
        var options = Bind([]);

        Assert.Equal(Features.All, options.FeaturesOf(UserPlan.Plus));
        Assert.Empty(options.FeaturesOf(UserPlan.Standard));
        Assert.True(options.HasOnlyKnownFeatures());
    }

    [Fact]
    public void Configured_lists_replace_defaults_instead_of_extending_them()
    {
        var options = Bind(new()
        {
            ["Plans:Standard:Features:0"] = Features.AiImport,
            ["Plans:Plus:Features:0"] = Features.AiDrafts
        });

        Assert.Equal([Features.AiImport], options.FeaturesOf(UserPlan.Standard));
        Assert.Equal([Features.AiDrafts], options.FeaturesOf(UserPlan.Plus));
    }

    [Fact]
    public void Unknown_feature_keys_are_rejected()
    {
        var options = Bind(new() { ["Plans:Plus:Features:0"] = "ai_magic" });

        Assert.False(options.HasOnlyKnownFeatures());
    }
}
