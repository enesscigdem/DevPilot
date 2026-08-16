using DevPilot.Application.AiProviders;

namespace DevPilot.Tests;

public sealed class FakeAiProvider : IAiProvider
{
    public string ProviderName => "StubFakeProvider";

    public int SendAsyncCallCount { get; private set; }

    public string ResponseToReturn { get; set; } = string.Empty;

    public Exception? ExceptionToThrow { get; set; }

    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        SendAsyncCallCount++;

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        if (string.IsNullOrWhiteSpace(ResponseToReturn))
        {
            throw new InvalidOperationException("FakeAiProvider: ResponseToReturn was not set for test.");
        }

        return Task.FromResult(new AiResponse { Content = ResponseToReturn });
    }
}
