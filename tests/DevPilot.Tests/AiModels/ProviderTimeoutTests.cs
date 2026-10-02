using System.Net;
using System.Text;
using DevPilot.Application.AiProviders;
using DevPilot.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevPilot.Tests.AiModels;

/// <summary>
/// The provider call has one deadline for the whole attempt (headers and the entire stream) and one for a stream that
/// goes silent. Both are reported as events, retried, and a caller cancellation is never retried.
/// </summary>
public class ProviderTimeoutTests
{
    private const string GoodStream =
        "data: {\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n\n" +
        "data: [DONE]\n\n";

    private static (OpenAiCompatibleProvider Provider, ScriptedHandler Handler, List<AiAttemptEvent> Events, AiRequest Request) Setup(
        Func<int, CancellationToken, Task<HttpResponseMessage>> script,
        TimeSpan total,
        TimeSpan idle,
        int maxAttempts = 2)
    {
        var handler = new ScriptedHandler(script);
        var provider = new OpenAiCompatibleProvider(
            new SingleClientFactory(handler),
            new OpenAiCompatibleSettings
            {
                ProviderName = "test",
                DisplayName = "Test",
                BaseUrl = "https://api.example.com",
                Model = "m",
                ApiKey = "k",
                MaxAttempts = maxAttempts,
                BaseDelayMs = 1,
                TotalTimeout = total,
                StreamIdleTimeout = idle,
            },
            NullLogger.Instance);

        var events = new List<AiAttemptEvent>();
        var request = new AiRequest
        {
            UserPrompt = "x",
            OnAttempt = e =>
            {
                lock (events)
                {
                    events.Add(e);
                }

                return Task.CompletedTask;
            },
        };
        return (provider, handler, events, request);
    }

    [Fact]
    public async Task HeadersThatNeverArrive_HitTheTotalTimeout_AreReported_AndRetried()
    {
        var (provider, handler, events, request) = Setup(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            },
            total: TimeSpan.FromMilliseconds(150),
            idle: TimeSpan.FromSeconds(30));

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeFalse();
        response.FailureKind.Should().Be(AiFailureKind.TimeoutOrConnection);
        response.ErrorMessage.Should().Contain("total time limit");
        response.AttemptCount.Should().Be(2);
        handler.Calls.Should().Be(2);
        events.Select(e => (e.Attempt, e.Kind, e.WillRetry)).Should().Equal((1, "TotalTimeout", true), (2, "TotalTimeout", false));
    }

    [Fact]
    public async Task AStreamThatGoesSilent_HitsTheIdleTimeout_NotTheTotalOne()
    {
        var (provider, _, events, request) = Setup(
            (_, _) => Task.FromResult(Stream(new TrickleStream(initial: "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n", intervalMs: null))),
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromMilliseconds(200));

        var response = await provider.SendAsync(request);

        response.FailureKind.Should().Be(AiFailureKind.TimeoutOrConnection);
        response.ErrorMessage.Should().Contain("sent no data");
        events.Should().OnlyContain(e => e.Kind == "IdleTimeout");
        events.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASlowStreamThatKeepsTalking_IsStillStoppedByTheTotalTimeout()
    {
        var (provider, _, events, request) = Setup(
            (_, _) => Task.FromResult(Stream(new TrickleStream(initial: "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n", intervalMs: 20))),
            total: TimeSpan.FromMilliseconds(250),
            idle: TimeSpan.FromSeconds(5));

        var response = await provider.SendAsync(request);

        response.ErrorMessage.Should().Contain("total time limit");
        events.Should().OnlyContain(e => e.Kind == "TotalTimeout");
    }

    [Fact]
    public async Task AnIdleAttempt_IsRetried_AndTheNextAttemptCanSucceed()
    {
        var (provider, handler, events, request) = Setup(
            (attempt, _) => Task.FromResult(attempt == 1
                ? Stream(new TrickleStream(initial: string.Empty, intervalMs: null))
                : Stream(new MemoryStream(Encoding.UTF8.GetBytes(GoodStream)))),
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromMilliseconds(150));

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeTrue();
        response.Content.Should().Be("ok");
        response.AttemptCount.Should().Be(2);
        handler.Calls.Should().Be(2);
        events.Should().ContainSingle().Which.Should().Match<AiAttemptEvent>(e => e.Kind == "IdleTimeout" && e.WillRetry && e.Attempt == 1);
    }

    [Fact]
    public async Task CancellingWhileTheStreamIsOpen_StopsAtOnce_AndIsNeverRetried()
    {
        var (provider, handler, events, request) = Setup(
            (_, _) => Task.FromResult(Stream(new TrickleStream(initial: "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n", intervalMs: null))),
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromSeconds(30),
            maxAttempts: 4);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var response = await provider.SendAsync(request, cts.Token);

        response.FailureKind.Should().Be(AiFailureKind.Cancelled);
        handler.Calls.Should().Be(1, "a cancelled call must not be retried");
        events.Should().ContainSingle().Which.Should().Match<AiAttemptEvent>(e => e.Kind == "Cancelled" && !e.WillRetry);
    }

    [Fact]
    public async Task CancellingWhileWaitingForHeaders_IsNeverRetried()
    {
        var (provider, handler, events, request) = Setup(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            },
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromSeconds(30),
            maxAttempts: 4);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var response = await provider.SendAsync(request, cts.Token);

        response.FailureKind.Should().Be(AiFailureKind.Cancelled);
        handler.Calls.Should().Be(1);
        events.Select(e => e.Kind).Should().Equal("Cancelled");
    }

    [Fact]
    public async Task ARetriedProviderError_IsReportedWithItsStatusCode_NotAsATimeout()
    {
        var (provider, _, events, request) = Setup(
            (attempt, _) => Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : Stream(new MemoryStream(Encoding.UTF8.GetBytes(GoodStream)))),
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromSeconds(30));

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeTrue();
        var seen = events.Should().ContainSingle().Subject;
        seen.Kind.Should().Be("HttpStatus");
        seen.StatusCode.Should().Be(503);
        seen.WillRetry.Should().BeTrue();
    }

    [Fact]
    public async Task AFailingObserver_NeverBreaksTheCall()
    {
        var (provider, _, _, request) = Setup(
            (attempt, _) => Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : Stream(new MemoryStream(Encoding.UTF8.GetBytes(GoodStream)))),
            total: TimeSpan.FromSeconds(30),
            idle: TimeSpan.FromSeconds(30));
        request.OnAttempt = _ => throw new InvalidOperationException("observer exploded");

        var response = await provider.SendAsync(request);

        response.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TheAttemptEventsReadClearlyInTheExecutionFeed()
    {
        AiExecutionBinding.Describe(new AiAttemptEvent(1, 4, "TotalTimeout", 300_000, true, 2000, Model: "gemini-3.7-flash-high"))
            .Should().Be("Provider call exceeded the total time limit after 300s (attempt 1/4) [gemini-3.7-flash-high]; retrying in 2s.");
        AiExecutionBinding.Describe(new AiAttemptEvent(2, 4, "IdleTimeout", 91_000, false))
            .Should().Contain("stopped sending data").And.Contain("not retried");
        AiExecutionBinding.Describe(new AiAttemptEvent(1, 4, "Cancelled", 4_000, false))
            .Should().Contain("cancelled").And.Contain("not retried");
        AiExecutionBinding.Describe(new AiAttemptEvent(1, 4, "HttpStatus", 1_000, true, 1000, 503))
            .Should().Contain("HTTP 503");
    }

    private static HttpResponseMessage Stream(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _script;
        private int _calls;

        public ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> script) => _script = script;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _script(Interlocked.Increment(ref _calls), cancellationToken);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>
    /// Sends the initial text, then either nothing more (a stalled stream) or one more SSE comment every
    /// <c>intervalMs</c> (a stream that is alive but never finishes). Reads honour cancellation like a real socket.
    /// </summary>
    private sealed class TrickleStream : Stream
    {
        private readonly int? _intervalMs;
        private byte[] _pending;

        public TrickleStream(string initial, int? intervalMs)
        {
            _pending = Encoding.UTF8.GetBytes(initial);
            _intervalMs = intervalMs;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.Length == 0)
            {
                await Task.Delay(_intervalMs ?? Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                _pending = Encoding.UTF8.GetBytes(": keep-alive\n");
            }

            var count = Math.Min(buffer.Length, _pending.Length);
            _pending.AsMemory(0, count).CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
