using System.Net;
using DevPilot.Application.AiProviders;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
    public async Task SendAsync_SuccessfulResponse_ParsesContentAndTokens()
    {
        var jsonResponse = """
            {
              "model": "kimi-k2.7-code",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "Hello from Kimi!"
                  }
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
            Content = new StringContent(jsonResponse)
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Hi" });

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Be("Hello from Kimi!");
        response.InputTokens.Should().Be(10);
        response.OutputTokens.Should().Be(5);
        handler.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task SendAsync_TransientStatusCode_RetriesUpToMaxAttempts(HttpStatusCode statusCode)
    {
        var httpResponse = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":\"transient error\"}")
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Test transient retry" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be($"Kimi API returned status code {(int)statusCode}.");
        handler.CallCount.Should().Be(4); // 1 initial + 3 retries
    }

    [Fact]
    public async Task SendAsync_NonTransient400BadRequest_DoesNotRetry()
    {
        var httpResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad prompt\"}")
        };

        var handler = new MockHttpMessageHandler(httpResponse);
        var factory = new MockHttpClientFactory(handler);

        var provider = new KimiAiProvider(factory, _configuration, NullLogger<KimiAiProvider>.Instance);

        var response = await provider.SendAsync(new AiRequest { UserPrompt = "Bad request" });

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Be("Kimi API returned status code 400.");
        handler.CallCount.Should().Be(1); // No retries for normal 400
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly MockHttpMessageHandler _handler;

        public MockHttpClientFactory(MockHttpMessageHandler handler)
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
        private readonly HttpResponseMessage _response;

        public int CallCount { get; private set; }

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }
}
