using System.Text.Json;
using DevPilot.Application.AiProviders;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.AiModels;

public class VendorAdapterTests
{
    // ---------------------------------- Claude ---------------------------------- //

    private static string ClaudeStream(string stopReason, params string[] textChunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("event: message_start\n");
        sb.Append("data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-test-1\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}\n\n");
        sb.Append("event: content_block_start\n");
        sb.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        foreach (var chunk in textChunks)
        {
            sb.Append("event: content_block_delta\n");
            sb.Append($"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{JsonSerializer.Serialize(chunk)}}}}}\n\n");
        }

        sb.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
        sb.Append("event: message_delta\n");
        sb.Append($"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stopReason}\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":5}}}}\n\n");
        sb.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return sb.ToString();
    }

    private static AnthropicAiProvider NewClaude(FakeHttpServer server, int maxAttempts = 1) =>
        new(new AiEndpointSettings
        {
            ProviderName = "claude",
            DisplayName = "Claude",
            BaseUrl = server.BaseUrl,
            Model = "claude-test",
            ApiKey = "sk-ant-test-key",
            MaxAttempts = maxAttempts,
        }, NullLogger.Instance);

    [Fact]
    public async Task Claude_StreamedAnswer_IsAssembledWithUsage()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, ClaudeStream("end_turn", "Hello", " world")));

        var response = await NewClaude(server).SendAsync(new AiRequest
        {
            SystemPrompt = "be brief",
            UserPrompt = "say hi",
            MaxTokens = 1234,
        });

        response.IsSuccess.Should().BeTrue(response.ErrorMessage);
        response.Content.Should().Be("Hello world");
        response.Model.Should().Be("claude-test-1");
        response.InputTokens.Should().Be(12);
        response.OutputTokens.Should().Be(5);

        var sent = server.Requests.Single();
        sent.RequestLine.Should().StartWith("POST /v1/messages");
        sent.Headers["x-api-key"].Should().Be("sk-ant-test-key");
        sent.Body.Should().Contain("\"max_tokens\":1234").And.Contain("\"stream\":true").And.Contain("be brief");
    }

    [Fact]
    public async Task Claude_MaxTokensStop_IsReportedAsTokenLimit()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, ClaudeStream("max_tokens", "partial")));

        var response = await NewClaude(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.TokenLimitExceeded);
        response.Content.Should().Be("partial");
    }

    [Fact]
    public async Task Claude_RefusalStop_IsPermanentFailure()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, ClaudeStream("refusal")));

        var response = await NewClaude(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.Permanent);
        response.ErrorMessage.Should().Contain("declined");
    }

    [Theory]
    [InlineData(401, AiFailureKind.Permanent)]
    [InlineData(404, AiFailureKind.Permanent)]
    [InlineData(429, AiFailureKind.RateLimited)]
    [InlineData(529, AiFailureKind.TransientServiceUnavailable)]
    public async Task Claude_HttpErrors_AreClassified(int status, AiFailureKind expected)
    {
        const string errorBody = """{"type":"error","error":{"type":"api_error","message":"nope"}}""";
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(status, errorBody, "application/json", "Retry-After: 0\r\n"));

        var response = await NewClaude(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(expected);
    }

    [Fact]
    public async Task Claude_WithoutKey_FailsBeforeAnyRequest()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, ClaudeStream("end_turn", "x")));
        var provider = new AnthropicAiProvider(new AiEndpointSettings
        {
            ProviderName = "claude", DisplayName = "Claude", BaseUrl = server.BaseUrl, Model = "m",
        }, NullLogger.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        server.Requests.Should().BeEmpty();
    }

    // ---------------------------------- Gemini ---------------------------------- //

    private static string GeminiChunk(string text, string? finish = null, bool thought = false, int? prompt = null, int? candidates = null, int? thoughts = null)
    {
        var part = thought ? $"{{\"text\":{JsonSerializer.Serialize(text)},\"thought\":true}}" : $"{{\"text\":{JsonSerializer.Serialize(text)}}}";
        var finishPart = finish is null ? string.Empty : $",\"finishReason\":\"{finish}\"";
        var usage = prompt is null ? string.Empty
            : $",\"usageMetadata\":{{\"promptTokenCount\":{prompt},\"candidatesTokenCount\":{candidates ?? 0}{(thoughts is null ? "" : $",\"thoughtsTokenCount\":{thoughts}")}}}";
        return $"data: {{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{part}]}}{finishPart}}}]{usage},\"modelVersion\":\"gemini-test-001\"}}\n\n";
    }

    private static GeminiAiProvider NewGemini(FakeHttpServer server, int maxAttempts = 1) =>
        new(new SimpleClientFactory(),
            new AiEndpointSettings
            {
                ProviderName = "gemini", DisplayName = "Gemini", BaseUrl = server.BaseUrl, Model = "gemini-test",
                ApiKey = "g-key", MaxAttempts = maxAttempts,
            },
            NullLogger.Instance);

    [Fact]
    public async Task Gemini_StreamedAnswer_SkipsThoughtsAndCountsThinkingAsOutput()
    {
        var body = GeminiChunk("reasoning...", thought: true)
                   + GeminiChunk("Hello")
                   + GeminiChunk(" world", finish: "STOP", prompt: 20, candidates: 4, thoughts: 6);
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, body));

        var response = await NewGemini(server).SendAsync(new AiRequest { SystemPrompt = "sys", UserPrompt = "hi", MaxTokens = 500 });

        response.IsSuccess.Should().BeTrue(response.ErrorMessage);
        response.Content.Should().Be("Hello world");
        response.InputTokens.Should().Be(20);
        response.OutputTokens.Should().Be(10);
        response.ReasoningTokens.Should().Be(6);
        response.Model.Should().Be("gemini-test-001");

        var sent = server.Requests.Single();
        sent.RequestLine.Should().StartWith("POST /v1beta/models/gemini-test:streamGenerateContent?alt=sse");
        sent.Headers["x-goog-api-key"].Should().Be("g-key");
        sent.Body.Should().Contain("\"maxOutputTokens\":500").And.Contain("systemInstruction");
    }

    [Fact]
    public async Task Gemini_MaxTokens_IsReportedAsTokenLimit()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, GeminiChunk("part", finish: "MAX_TOKENS")));

        var response = await NewGemini(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.FailureKind.Should().Be(AiFailureKind.TokenLimitExceeded);
        response.Content.Should().Be("part");
    }

    [Fact]
    public async Task Gemini_SafetyStop_IsPermanent()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(200, GeminiChunk("", finish: "SAFETY")));

        var response = await NewGemini(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.Permanent);
    }

    [Fact]
    public async Task Gemini_RateLimit_IsRetriedThenSucceeds()
    {
        using var server = new FakeHttpServer(
            new FakeHttpServer.Reply(429, """{"error":{"message":"slow down"}}""", "application/json", "Retry-After: 0\r\n"),
            new FakeHttpServer.Reply(200, GeminiChunk("ok", finish: "STOP", prompt: 1, candidates: 1)));

        var response = await NewGemini(server, maxAttempts: 2).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeTrue(response.ErrorMessage);
        response.AttemptCount.Should().Be(2);
        server.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(400, AiFailureKind.Permanent)]
    [InlineData(403, AiFailureKind.Permanent)]
    [InlineData(429, AiFailureKind.RateLimited)]
    [InlineData(503, AiFailureKind.TransientServiceUnavailable)]
    public async Task Gemini_HttpErrors_AreClassified_AndErrorTextIsExtracted(int status, AiFailureKind expected)
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(status, """{"error":{"message":"bad things"}}""", "application/json", "Retry-After: 0\r\n"));

        var response = await NewGemini(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(expected);
        response.ErrorMessage.Should().Contain("bad things");
    }

    [Fact]
    public async Task Gemini_AWebPageInsteadOfAnApiAnswer_IsNotDumpedIntoTheErrorMessage()
    {
        using var server = new FakeHttpServer(new FakeHttpServer.Reply(404, "<!DOCTYPE html><html><body>Not Found</body></html>", "text/html"));

        var response = await NewGemini(server).SendAsync(new AiRequest { UserPrompt = "x" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain("web page").And.NotContain("<html").And.NotContain("DOCTYPE");
    }

    private sealed class SimpleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
