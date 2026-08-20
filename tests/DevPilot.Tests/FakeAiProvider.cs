using DevPilot.Application.AiProviders;

namespace DevPilot.Tests;

public sealed class FakeAiProvider : IAiProvider
{
    public string ProviderName { get; set; } = "StubFakeProvider";

    public int SendAsyncCallCount { get; private set; }

    public string ResponseToReturn { get; set; } = string.Empty;

    public Queue<string> ResponsesToReturn { get; } = new();

    public Queue<AiResponse> StructuredResponsesToReturn { get; } = new();

    public Func<AiRequest, CancellationToken, Task<AiResponse>>? CustomHandler { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public bool IsSuccessToReturn { get; set; } = true;

    public string? ErrorMessageToReturn { get; set; }

    public int? StatusCodeToReturn { get; set; }

    public AiFailureKind FailureKindToReturn { get; set; } = AiFailureKind.None;

    public List<AiRequest> ReceivedRequests { get; } = new();

    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        SendAsyncCallCount++;
        ReceivedRequests.Add(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new AiResponse
            {
                Provider = ProviderName,
                IsSuccess = false,
                FailureKind = AiFailureKind.Cancelled,
                ErrorMessage = "Request was cancelled."
            });
        }

        if (CustomHandler != null)
        {
            return CustomHandler(request, cancellationToken);
        }

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        if (StructuredResponsesToReturn.Count > 0)
        {
            var structured = StructuredResponsesToReturn.Dequeue();
            if (string.IsNullOrWhiteSpace(structured.Provider))
            {
                structured.Provider = ProviderName;
            }
            return Task.FromResult(structured);
        }

        string content = string.Empty;
        if (ResponsesToReturn.Count > 0)
        {
            content = ResponsesToReturn.Dequeue();
        }
        else if (!string.IsNullOrEmpty(ResponseToReturn))
        {
            content = ResponseToReturn;
        }
        else if (IsSuccessToReturn)
        {
            throw new InvalidOperationException("FakeAiProvider: ResponseToReturn was not set for test.");
        }

        return Task.FromResult(new AiResponse
        {
            Provider = ProviderName,
            Content = content,
            IsSuccess = IsSuccessToReturn,
            StatusCode = StatusCodeToReturn ?? (IsSuccessToReturn ? 200 : (int?)null),
            FailureKind = FailureKindToReturn,
            ErrorMessage = ErrorMessageToReturn,
        });
    }
}
