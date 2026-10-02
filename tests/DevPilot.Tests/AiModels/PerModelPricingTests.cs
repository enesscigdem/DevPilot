using System.Text.Json;
using DevPilot.Application.Executions.Models;
using DevPilot.Application.Executions.Options;
using DevPilot.Application.Executions.Services;
using DevPilot.Domain.Entities;
using DevPilot.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests.AiModels;

public class PerModelPricingTests
{
    private static AiPricingOptions Prices(decimal? globalIn = null, decimal? globalOut = null, params AiModelPrice[] models) =>
        new()
        {
            InputPerMillionTokensUsd = globalIn,
            OutputPerMillionTokensUsd = globalOut,
            ModelPrices = models,
        };

    private static ExecutionActivity Call(string? model, int input, int output) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Stage = ExecutionStage.DeveloperAgent,
            Status = ExecutionActivityStatus.Completed,
            Message = "Provider call completed: Generation.",
            CreatedAt = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new ExecutionActivityMetadata(
                EventKind: "ProviderCall",
                ProviderCallKind: "Generation",
                Model: model,
                InputTokens: input,
                OutputTokens: output,
                StageDurationMs: 100)),
        };

    [Theory]
    [InlineData("deepseek-chat", true)]
    [InlineData("DEEPSEEK-CHAT", true)]
    [InlineData("deepseek-chat-0324", true)]
    [InlineData("deepseek-reasoner", false)]
    [InlineData(null, false)]
    public void TryGetPrice_MatchesExactOrVersionedNames(string? model, bool expected)
    {
        var pricing = Prices(models: new AiModelPrice("deepseek-chat", 0.3m, 1.2m));

        pricing.TryGetPrice(model, out var input, out var output).Should().Be(expected);
        if (expected)
        {
            (input, output).Should().Be((0.3m, 1.2m));
        }
    }

    [Fact]
    public void TryGetPrice_PrefersTheLongestMatchingName()
    {
        var pricing = Prices(models: new[]
        {
            new AiModelPrice("gpt-5", 1m, 2m),
            new AiModelPrice("gpt-5-mini", 0.1m, 0.2m),
        });

        pricing.TryGetPrice("gpt-5-mini-2026-01-01", out var input, out _).Should().BeTrue();
        input.Should().Be(0.1m);
    }

    [Fact]
    public void TryGetPrice_FallsBackToTheGlobalPrice_ForUnlistedModels()
    {
        var pricing = Prices(3m, 15m, new AiModelPrice("cheap-model", 0.1m, 0.2m));

        pricing.TryGetPrice("other-model", out var input, out var output).Should().BeTrue();
        (input, output).Should().Be((3m, 15m));
    }

    [Fact]
    public void Usage_PricesEachCallByTheModelThatServedIt()
    {
        var pricing = Prices(models: new[]
        {
            new AiModelPrice("model-a", 1m, 2m),
            new AiModelPrice("model-b", 10m, 20m),
        });

        var usage = ExecutionVerdictBuilder.AggregateUsage(
            new List<ExecutionActivity>
            {
                Call("model-a", 1_000_000, 500_000), // 1 + 1 = 2
                Call("model-b", 100_000, 50_000),    // 1 + 1 = 2
            },
            pricing);

        usage.EstimatedCostUsd.Should().Be(4m);
    }

    [Fact]
    public void Usage_WithOneUnpricedModel_ReportsNoCost_InsteadOfAnUnderestimate()
    {
        var pricing = Prices(models: new AiModelPrice("model-a", 1m, 2m));

        var usage = ExecutionVerdictBuilder.AggregateUsage(
            new List<ExecutionActivity>
            {
                Call("model-a", 1_000_000, 0),
                Call("model-without-price", 1_000_000, 0),
            },
            pricing);

        usage.EstimatedCostUsd.Should().BeNull();
        usage.TotalTokens.Should().Be(2_000_000);
    }

    [Fact]
    public void Usage_WithGlobalPriceOnly_BehavesAsBefore()
    {
        var usage = ExecutionVerdictBuilder.AggregateUsage(
            new List<ExecutionActivity> { Call("anything", 1000, 200) },
            Prices(3m, 15m));

        usage.EstimatedCostUsd.Should().Be(0.006m);
    }
}
