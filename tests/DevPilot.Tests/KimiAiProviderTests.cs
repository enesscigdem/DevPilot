using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DevPilot.Application.AiProviders;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests;

public class KimiAiProviderTests
{
    private readonly IConfiguration _configuration;

    public KimiAiProviderTests()
    {
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["AiProvider:Kimi:BaseUrl"] = "https://api.moonshot.cn",
            ["AiProvider:Kimi:Model"] = "kimi-k2.7-code",
            ["AiProvider:Kimi:ApiKey"] = "test-api-key-12345",
            ["AiProvider:Kimi:Stream"] = "true",
            ["AiProvider:Kimi:BaseDelayMs"] = "1",
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();
    }

    [Fact]
    public async Task SendAsync_Unconfigured_ReturnsFailedAiResponse()
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, emptyConfig, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Hello" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain("Kimi provider is not configured");
    }

    [Fact]
    public async Task SendAsync_StreamingSuccessfulResponse_ParsesAndCombinesChunks()
    {
        var sseStream = """
            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","model":"kimi-k2.7-code","choices":[{"index":0,"delta":{"role":"assistant","content":"{\n  \"files\": ["},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","model":"kimi-k2.7-code","choices":[{"index":0,"delta":{"content":"\n    {\n      \"filePath\": \"App.cs\""},"finish_reason":null}]}

            data: {"id":"chatcmpl-1","object":"chat.completion.chunk","model":"kimi-k2.7-code","choices":[{"index":0,"delta":{"content":"\n    }\n  ]\n}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":120,"completion_tokens":45}}

            data: [DONE]
            """;

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseStream, Encoding.UTF8, "text/event-stream")
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Generate code" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Contain("\"filePath\": \"App.cs\"");
        response.FinishReason.Should().Be("stop");
        response.InputTokens.Should().Be(120);
        response.OutputTokens.Should().Be(45);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_NonStreamingSuccessfulResponse_ParsesContentAndTokens()
    {
        var nonStreamConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Kimi:BaseUrl"] = "https://api.moonshot.cn",
                ["AiProvider:Kimi:Model"] = "kimi-k2.7-code",
                ["AiProvider:Kimi:ApiKey"] = "test-api-key-12345",
                ["AiProvider:Kimi:Stream"] = "false",
            })
            .Build();

        var jsonResponse = """
            {
              "model": "kimi-k2.7-code",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "Hello from Kimi non-stream!"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 10,
                "completion_tokens": 5
              }
            }
            """;

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, nonStreamConfig, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Hi" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Be("Hello from Kimi non-stream!");
        response.FinishReason.Should().Be("stop");
        response.InputTokens.Should().Be(10);
        response.OutputTokens.Should().Be(5);
        handler.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Kimi HTTP 429 rate limited after 4 attempts.", AiFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, "Kimi HTTP 502 bad gateway after 4 attempts.", AiFailureKind.TransientServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Kimi HTTP 503 service unavailable after 4 attempts.", AiFailureKind.TransientServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, "Kimi HTTP 504 gateway timeout after 4 attempts.", AiFailureKind.TransientServiceUnavailable)]
    public async Task SendAsync_TransientStatusCode_RetriesUpToMaxAttempts(HttpStatusCode statusCode, string expectedError, AiFailureKind expectedKind)
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":{\"code\":\"service_unavailable\",\"type\":\"server_error\",\"message\":\"Server busy\"}}")
        });

        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Test transient retry" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be(expectedError);
        response.StatusCode.Should().Be((int)statusCode);
        response.AttemptCount.Should().Be(4);
        response.FailureKind.Should().Be(expectedKind);
        response.IsTransient.Should().BeTrue();
        handler.CallCount.Should().Be(4); // 1 initial + 3 retries
    }

    [Fact]
    public async Task SendAsync_PermanentQuotaExhaustion429_DoesNotRetry()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"code\":\"insufficient_quota\",\"type\":\"insufficient_quota\",\"message\":\"Quota exhausted\"}}")
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Quota test" });

        response.IsSuccess.Should().BeFalse();
        response.StatusCode.Should().Be(429);
        response.AttemptCount.Should().Be(1);
        response.FailureKind.Should().Be(AiFailureKind.Permanent);
        response.IsTransient.Should().BeFalse();
        handler.CallCount.Should().Be(1); // No retries for permanent quota error
    }

    [Fact]
    public void GetDelayMilliseconds_WithValidRetryAfter_ReturnsClampedDelay()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));

        var delay = KimiAiProvider.GetDelayMilliseconds(response, attempt: 1, baseDelayMs: 1000, maxRetryAfterMs: 30000);

        delay.Should().Be(50);
    }

    [Fact]
    public void GetDelayMilliseconds_WithExcessiveRetryAfter_ClampsToMaxLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(60000));

        var delay = KimiAiProvider.GetDelayMilliseconds(response, attempt: 1, baseDelayMs: 1000, maxRetryAfterMs: 30000);

        delay.Should().Be(30000);
    }

    [Fact]
    public void GetDelayMilliseconds_WithoutRetryAfter_UsesExponentialBackoffWithJitter()
    {
        var delay1 = KimiAiProvider.GetDelayMilliseconds(null, attempt: 1, baseDelayMs: 1000, maxRetryAfterMs: 30000);
        var delay2 = KimiAiProvider.GetDelayMilliseconds(null, attempt: 2, baseDelayMs: 1000, maxRetryAfterMs: 30000);
        var delay3 = KimiAiProvider.GetDelayMilliseconds(null, attempt: 3, baseDelayMs: 1000, maxRetryAfterMs: 30000);

        // Attempt 1: 1000 + jitter (0..250) -> [1000..1250]
        delay1.Should().BeInRange(1000, 1250);

        // Attempt 2: 2000 + jitter (0..500) -> [2000..2500]
        delay2.Should().BeInRange(2000, 2500);

        // Attempt 3: 4000 + jitter (0..500) -> [4000..4500]
        delay3.Should().BeInRange(4000, 4500);
    }

    [Fact]
    public async Task SendAsync_TooManyRequests429WithRequestId_SurfacesClassifiedRateLimitAndRequestId()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Rate limit reached\"}}")
            };
            msg.Headers.Add("x-request-id", "req-rate-12345");
            return msg;
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Rate limited prompt" });

        response.IsSuccess.Should().BeFalse();
        response.StatusCode.Should().Be(429);
        response.AttemptCount.Should().Be(4);
        response.RequestId.Should().Be("req-rate-12345");
        response.ErrorMessage.Should().Be("Kimi HTTP 429 rate limited after 4 attempts (RequestId: req-rate-12345).");
    }

    [Fact]
    public async Task SendAsync_Transient503ThenSuccess_ReturnsSuccessOnRetry()
    {
        var response503 = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}")
        };
        response503.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));

        var sseStream = """
            data: {"id":"chatcmpl-2","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Success after 503!"},"finish_reason":"stop"}]}

            data: [DONE]
            """;
        var response200 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseStream, Encoding.UTF8, "text/event-stream")
        };

        var sequenceHandler = new SequenceHttpMessageHandler(response503, response200);
        var factory = new MockHttpClientFactory(sequenceHandler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Retry test" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Be("Success after 503!");
        response.AttemptCount.Should().Be(2);
        sequenceHandler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_HonorsRetryAfterHeader()
    {
        var response429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}")
        };
        response429.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));

        var sseStream = """
            data: {"id":"chatcmpl-3","choices":[{"index":0,"delta":{"content":"Done after rate limit."},"finish_reason":"stop"}]}

            data: [DONE]
            """;
        var response200 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseStream, Encoding.UTF8, "text/event-stream")
        };

        var sequenceHandler = new SequenceHttpMessageHandler(response429, response200);
        var factory = new MockHttpClientFactory(sequenceHandler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Rate limit test" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Be("Done after rate limit.");
        sequenceHandler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_InterruptedStreamWithoutDone_ReturnsClassifiedSseEndedError()
    {
        var brokenStream = """
            data: {"id":"chatcmpl-broken","choices":[{"index":0,"delta":{"content":"partial json {"},"finish_reason":null}]}
            """; // Stream cut off without [DONE] and no finish_reason

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(brokenStream, Encoding.UTF8, "text/event-stream")
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Stream interrupt test" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain("Kimi SSE stream ended before [DONE] after 4 attempts");
        response.AttemptCount.Should().Be(4);
    }

    [Fact]
    public async Task SendAsync_EmptyStreamResponse_RetriesAndFailsSafelyIfAllAttemptsEmpty()
    {
        var emptyStream = "data: [DONE]\n";

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(emptyStream, Encoding.UTF8, "text/event-stream")
        });
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Empty stream test" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("Kimi returned empty content after 4 attempts.");
        response.AttemptCount.Should().Be(4);
        handler.CallCount.Should().Be(4); // Retries on empty content
    }

    [Fact]
    public async Task SendAsync_RequestTimeout_IsDistinguishableFromHttpFailure()
    {
        var handler = new MockHttpMessageHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new TaskCanceledException("HttpClient timeout")));
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Timeout prompt" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain("Kimi request timed out after");
        response.ErrorMessage.Should().Contain("(4 attempts)");
        response.AttemptCount.Should().Be(4);
    }

    [Fact]
    public async Task SendAsync_ConfiguredCancellation_IsNotReportedAsProviderFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Cancel prompt" }, cts.Token);

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("Kimi request was cancelled.");
    }

    [Fact]
    public async Task SendAsync_NonStreamingParseFailure_IsDistinguishableFromEmptyContent()
    {
        var nonStreamConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Kimi:BaseUrl"] = "https://api.moonshot.cn",
                ["AiProvider:Kimi:Model"] = "kimi-k2.7-code",
                ["AiProvider:Kimi:ApiKey"] = "test-api-key-12345",
                ["AiProvider:Kimi:Stream"] = "false",
            })
            .Build();

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("invalid json body {", Encoding.UTF8, "application/json")
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, nonStreamConfig, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Parse failure test" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("Kimi response parsing failed after 1 attempt(s).");
    }

    [Fact]
    public async Task SendAsync_DiagnosticsNeverContainApiKeyOrSensitiveBearerTokens()
    {
        var testLogger = new TestListLogger<KimiAiProvider>();
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"upstream server issue with Bearer secret-token-xyz and ghp_abc123\"}")
        });
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, testLogger);

        await provider.SendAsync(new AiRequest { UserPrompt = "Secret prompt" });

        foreach (var logMessage in testLogger.Logs)
        {
            logMessage.Should().NotContain("test-api-key-12345", "API key must never be logged");
            logMessage.Should().NotContain("secret-token-xyz", "Bearer tokens must be redacted");
            logMessage.Should().NotContain("ghp_abc123", "GitHub tokens must be redacted");
        }
    }

    [Fact]
    public async Task SendAsync_DeveloperAgentRequestWithMaxTokens16384_SerializesMaxTokensInJsonPayload()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async req =>
        {
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var request = new AiRequest
        {
            UserPrompt = "Develop feature",
            MaxTokens = 16384
        };

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeTrue();
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"max_tokens\":16384");
    }

    [Fact]
    public async Task SendAsync_GeneralRequestWithoutMaxTokens_OmitsMaxTokensInJsonPayload()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async req =>
        {
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var request = new AiRequest
        {
            UserPrompt = "Ask simple question"
            // MaxTokens is not set (null)
        };

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeTrue();
        capturedBody.Should().NotBeNull();
        capturedBody.Should().NotContain("\"max_tokens\"");
    }

    [Fact]
    public async Task SendAsync_StreamingWithReasoningContentAndFinalContent_AccumulatesContentOnlyInAiResponse()
    {
        var sseStream = """
            data: {"id":"chatcmpl-r1","choices":[{"index":0,"delta":{"reasoning_content":"Let me think about this structured edit... First we need Calculator.cs"},"finish_reason":null}]}

            data: {"id":"chatcmpl-r1","choices":[{"index":0,"delta":{"reasoning_content":"\nNow let us formulate the JSON response."},"finish_reason":null}]}

            data: {"id":"chatcmpl-r1","choices":[{"index":0,"delta":{"content":"{\n  \"files\": [\n    {\n      \"filePath\": \"Calculator.cs\""},"finish_reason":null}]}

            data: {"id":"chatcmpl-r1","choices":[{"index":0,"delta":{"content":"\n    }\n  ]\n}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":150,"completion_tokens":200}}

            data: [DONE]
            """;

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseStream, Encoding.UTF8, "text/event-stream")
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Generate edit" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().NotContain("Let me think about this", "Reasoning tokens must be excluded from final Content");
        response.Content.Should().NotContain("formulate the JSON response", "Reasoning tokens must be excluded from final Content");
        response.Content.Should().Contain("\"filePath\": \"Calculator.cs\"");
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task SendAsync_StreamingFinishReasonLength_ReturnsControlledErrorMessageWithoutTransportRetry()
    {
        var sseStream = """
            data: {"id":"chatcmpl-len","choices":[{"index":0,"delta":{"reasoning_content":"Thinking exhausted all tokens..."},"finish_reason":null}]}

            data: {"id":"chatcmpl-len","choices":[{"index":0,"delta":{"content":"{\n  \"files\": ["},"finish_reason":"length"}],"usage":{"prompt_tokens":200,"completion_tokens":4096}}

            data: [DONE]
            """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseStream, Encoding.UTF8, "text/event-stream")
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Exhaust tokens prompt" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("AI response exhausted the configured output token limit before producing a complete result.");
        response.FinishReason.Should().Be("length");
        handler.CallCount.Should().Be(1, "finish_reason 'length' is a token budget issue and must not be retried as a transport error");
    }

    [Fact]
    public async Task SendAsync_NonStreamingFinishReasonLength_ReturnsControlledErrorMessageWithoutTransportRetry()
    {
        var nonStreamConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Kimi:BaseUrl"] = "https://api.moonshot.cn",
                ["AiProvider:Kimi:Model"] = "kimi-k2.7-code",
                ["AiProvider:Kimi:ApiKey"] = "test-api-key-12345",
                ["AiProvider:Kimi:Stream"] = "false",
            })
            .Build();

        var jsonResponse = """
            {
              "model": "kimi-k2.7-code",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "{\n  \"files\": ["
                  },
                  "finish_reason": "length"
                }
              ],
              "usage": {
                "prompt_tokens": 100,
                "completion_tokens": 4096
              }
            }
            """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var factory = new MockHttpClientFactory(handler);
        var provider = new KimiAiProvider(factory, nonStreamConfig, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Non stream length test" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("AI response exhausted the configured output token limit before producing a complete result.");
        response.FinishReason.Should().Be("length");
        handler.CallCount.Should().Be(1, "finish_reason 'length' must not trigger retry");
    }

    [Fact]
    public async Task SendAsync_NonTransient400BadRequest_DoesNotRetry()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Invalid model name\"}}")
        });
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Bad request" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("Kimi HTTP 400 after 1 attempt.");
        response.StatusCode.Should().Be(400);
        response.AttemptCount.Should().Be(1);
        handler.CallCount.Should().Be(1); // No retries for normal 400
    }

    private sealed class TestListLogger<T> : ILogger<T>
    {
        public List<string> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Logs.Add(message);
        }
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public MockHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public int CallCount { get; private set; }

        public MockHttpMessageHandler(HttpResponseMessage response)
            : this(_ => Task.FromResult(response))
        {
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this(req => Task.FromResult(responseFactory(req)))
        {
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _responseFactory(request);
        }
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public int CallCount { get; private set; }

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count > 0)
            {
                return Task.FromResult(_responses.Dequeue());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
