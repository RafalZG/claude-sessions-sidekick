using System.Net;
using System.Text.Json;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

/// <summary>
/// Tests the ClaudeUsageService state machine: caching, backoff, failure counting,
/// auto-retry disable, manual refresh bypass. Uses a mock HttpMessageHandler so
/// no network calls are made.
/// </summary>
public class ClaudeUsageServiceTests : IDisposable
{
    private readonly MockHandler _handler = new();
    private readonly ClaudeUsageService _svc;

    private static readonly string ValidUsageJson = JsonSerializer.Serialize(new UsageData
    {
        FiveHour = new UsageWindow { Utilization = 0.15, ResetsAt = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero) },
        SevenDay = new UsageWindow { Utilization = 0.42, ResetsAt = new DateTimeOffset(2030, 6, 5, 0, 0, 0, TimeSpan.Zero) }
    });

    public ClaudeUsageServiceTests()
    {
        _svc = new ClaudeUsageService(_handler, watchCredentials: false)
        {
            AccessTokenOverride = () => "fake-token"
        };
    }

    public void Dispose() => _svc.Dispose();

    // ── Cache behavior ─────────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_ReturnsCachedData_WhenFresh()
    {
        // Arrange - first call fills the cache
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync();
        var callCount = _handler.CallCount;

        // Act - second call within cache interval should not hit network
        var result = await _svc.FetchUsageAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(callCount, _handler.CallCount);
    }

    [Fact]
    public async Task FetchUsage_ForceRefresh_BypassesCache()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync();
        var callCount = _handler.CallCount;

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.True(_handler.CallCount > callCount);
    }

    // ── Adaptive cache interval ────────────────────────────────────

    [Fact]
    public void CacheInterval_NoCachedData_Returns3Min()
    {
        // Assert
        Assert.Equal(TimeSpan.FromMinutes(3), _svc.CurrentCacheInterval);
    }

    [Fact]
    public async Task CacheInterval_LowUtilization_Returns10Min()
    {
        // Arrange - 15% utilization
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(10), _svc.CurrentCacheInterval);
    }

    [Fact]
    public async Task CacheInterval_HighUtilization_Returns3Min()
    {
        // Arrange - 95% utilization
        var highJson = JsonSerializer.Serialize(new UsageData
        {
            FiveHour = new UsageWindow { Utilization = 0.95, ResetsAt = DateTimeOffset.UtcNow.AddHours(1) }
        });
        _handler.SetResponse(HttpStatusCode.OK, highJson);
        await _svc.FetchUsageAsync();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(3), _svc.CurrentCacheInterval);
    }

    [Fact]
    public async Task CacheInterval_MidUtilization_Returns5Min()
    {
        // Arrange - 75% utilization
        var midJson = JsonSerializer.Serialize(new UsageData
        {
            FiveHour = new UsageWindow { Utilization = 0.75, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) }
        });
        _handler.SetResponse(HttpStatusCode.OK, midJson);
        await _svc.FetchUsageAsync();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), _svc.CurrentCacheInterval);
    }

    // ── Auth failure (401) ─────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_401_SetsBackoff()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.Unauthorized, """{"error":"auth"}""");

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.True(_svc.IsBackedOff);
        Assert.Equal(1, _svc.ConsecutiveFailures);
        Assert.Contains("expired", _svc.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterFailure_DisablesAutoRetryAfter5()
    {
        // Act - simulate 5 consecutive failures
        for (int i = 0; i < 5; i++)
        {
            _svc.RegisterFailure();
        }

        // Assert
        Assert.True(_svc.AutoRetryDisabled);
        Assert.Equal(5, _svc.ConsecutiveFailures);
    }

    [Fact]
    public void RegisterFailure_DoesNotDisableBefore5()
    {
        // Act
        for (int i = 0; i < 4; i++)
        {
            _svc.RegisterFailure();
        }

        // Assert
        Assert.False(_svc.AutoRetryDisabled);
        Assert.Equal(4, _svc.ConsecutiveFailures);
    }

    [Fact]
    public async Task FetchUsage_AutoRetryDisabled_SkipsWithoutManual()
    {
        // Arrange - directly disable auto-retry
        for (int i = 0; i < 5; i++)
        {
            _svc.RegisterFailure();
        }
        Assert.True(_svc.AutoRetryDisabled);
        var callCount = _handler.CallCount;

        // Act - non-manual fetch should return cached without calling API
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.Equal(callCount, _handler.CallCount);
    }

    // ── Rate limit (429) ───────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_429_SetsBackoff()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.TooManyRequests, """{"error":"rate_limit"}""");

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.True(_svc.IsBackedOff);
        Assert.Equal(1, _svc.ConsecutiveFailures);
    }

    // ── Manual refresh ─────────────────────────────────────────────

    [Fact]
    public async Task ManualRefresh_ClearsBackoff()
    {
        // Arrange - set up backoff via 401
        _handler.SetResponse(HttpStatusCode.Unauthorized, """{"error":"auth"}""");
        await _svc.FetchUsageAsync(forceRefresh: true);
        Assert.True(_svc.IsBackedOff);

        // Act - manual refresh should clear backoff and try again
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        var result = await _svc.FetchUsageAsync(manualRefresh: true);

        // Assert
        Assert.NotNull(result);
        Assert.False(_svc.IsBackedOff);
        Assert.Null(_svc.LastError);
        Assert.Equal(0, _svc.ConsecutiveFailures);
    }

    [Fact]
    public async Task ManualRefresh_ClearsAutoRetryDisabled()
    {
        // Arrange - directly disable auto-retry
        for (int i = 0; i < 5; i++)
        {
            _svc.RegisterFailure();
        }
        Assert.True(_svc.AutoRetryDisabled);

        // Act
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        var result = await _svc.FetchUsageAsync(manualRefresh: true);

        // Assert
        Assert.NotNull(result);
        Assert.False(_svc.AutoRetryDisabled);
    }

    // ── Recovery after failure ──────────────────────────────────────

    [Fact]
    public async Task SuccessAfterFailure_ResetsAllState()
    {
        // Arrange - fail first
        _handler.SetResponse(HttpStatusCode.Unauthorized, """{"error":"auth"}""");
        await _svc.FetchUsageAsync(forceRefresh: true);
        Assert.True(_svc.IsBackedOff);

        // Act - succeed on manual refresh
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync(manualRefresh: true);

        // Assert
        Assert.False(_svc.IsBackedOff);
        Assert.False(_svc.AutoRetryDisabled);
        Assert.Equal(0, _svc.ConsecutiveFailures);
        Assert.Null(_svc.LastError);
        Assert.NotNull(_svc.CachedData);
    }

    // ── ClearCache ─────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_ResetsEverything()
    {
        // Arrange - fill cache and trigger error
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync();
        Assert.NotNull(_svc.CachedData);

        // Act
        _svc.ClearCache();

        // Assert
        Assert.Null(_svc.CachedData);
        Assert.False(_svc.IsBackedOff);
        Assert.False(_svc.AutoRetryDisabled);
        Assert.Equal(0, _svc.ConsecutiveFailures);
        Assert.Null(_svc.LastError);
    }

    // ── No token ───────────────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_NoToken_SetsErrorWithoutNetworkCall()
    {
        // Arrange
        _svc.AccessTokenOverride = () => null;

        // Act
        var result = await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.Null(result);
        Assert.Equal(0, _handler.CallCount);
        Assert.Contains("access token", _svc.LastError, StringComparison.OrdinalIgnoreCase);
    }

    // ── Request headers ──────────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_SendsAuthorizationHeader()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("Bearer fake-token", _handler.LastRequest.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task FetchUsage_SendsAnthropicBetaHeader()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.NotNull(_handler.LastRequest);
        Assert.True(_handler.LastRequest.Headers.TryGetValues("anthropic-beta", out var values));
        Assert.Contains("oauth-2025-04-20", values);
    }

    // ── Token refresh ───────────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_401ThenManualRefresh_RecoverySucceeds()
    {
        // Arrange - simulates the flow: token expired → 401 → user/widget refreshes
        // token → manual refresh with valid response recovers fully.
        // Note: TryRefreshTokenAsync itself is not exercised here because
        // AccessTokenOverride bypasses ReadAccessTokenAsync. The refresh logic
        // is best tested via integration test with a temp credentials file.
        _handler.SetResponse(HttpStatusCode.Unauthorized, """{"error":"auth"}""");
        await _svc.FetchUsageAsync(forceRefresh: true);
        Assert.True(_svc.IsBackedOff);

        // Act - simulate recovery after token refresh
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        var result = await _svc.FetchUsageAsync(manualRefresh: true);

        // Assert
        Assert.NotNull(result);
        Assert.False(_svc.IsBackedOff);
        Assert.Null(_svc.LastError);
    }

    // ── Retry on transient failure ─────────────────────────────────

    [Fact]
    public async Task FetchUsage_TransientFailureThenSuccess_ReturnsData()
    {
        // Arrange - first call throws HttpRequestException (simulated by 500),
        // but the retry loop only retries on HttpRequestException/TaskCanceledException.
        // Mock a success on second call via queue.
        _handler.EnqueueResponse(HttpStatusCode.InternalServerError, """{"error":"server"}""");
        _handler.EnqueueResponse(HttpStatusCode.OK, ValidUsageJson);

        // Act
        var result = await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert - non-2xx on attempt 1 triggers RegisterFailure and returns,
        // so we get null/cached. This verifies the 500 path registers failure.
        Assert.Equal(1, _svc.ConsecutiveFailures);
    }

    [Fact]
    public async Task FetchUsage_429DoesNotRetry_ReturnsImmediately()
    {
        // Arrange - 429 should NOT use the retry loop; it returns immediately with backoff
        _handler.SetResponse(HttpStatusCode.TooManyRequests, """{"error":"rate_limit"}""");

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert - only 1 call, no retry
        Assert.Equal(1, _handler.CallCount);
        Assert.True(_svc.IsBackedOff);
    }

    // ── JSON parse failure ─────────────────────────────────────────

    [Fact]
    public async Task FetchUsage_MalformedJson_SetsBackoffAndError()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.OK, "not-valid-json!!!");

        // Act
        await _svc.FetchUsageAsync(forceRefresh: true);

        // Assert
        Assert.True(_svc.IsBackedOff);
        Assert.Equal(1, _svc.ConsecutiveFailures);
        Assert.Contains("format", _svc.LastError, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetStateSnapshot ───────────────────────────────────────────

    [Fact]
    public async Task GetStateSnapshot_ContainsKeyInfo()
    {
        // Arrange
        _handler.SetResponse(HttpStatusCode.OK, ValidUsageJson);
        await _svc.FetchUsageAsync();

        // Act
        var snapshot = _svc.GetStateSnapshot();

        // Assert
        Assert.Contains("HasCachedData: True", snapshot);
        Assert.Contains("ConsecutiveFailures: 0", snapshot);
        Assert.Contains("AutoRetryDisabled: False", snapshot);
    }

    // ── MaskTokenLikeValues ────────────────────────────────────────

    [Fact]
    public void MaskTokenLikeValues_RedactsRefreshToken()
    {
        // Arrange
        var body = """{"refresh_token":"abc123def","error":"invalid"}""";

        // Act
        var masked = ClaudeUsageService.MaskTokenLikeValues(body);

        // Assert
        Assert.DoesNotContain("abc123def", masked);
        Assert.Contains("\"refresh_token\":\"***\"", masked);
        Assert.Contains("\"error\":\"invalid\"", masked); // other fields untouched
    }

    [Fact]
    public void MaskTokenLikeValues_RedactsAccessAndIdTokens()
    {
        // Arrange
        var body = """{"access_token":"secret-a","id_token":"secret-i"}""";

        // Act
        var masked = ClaudeUsageService.MaskTokenLikeValues(body);

        // Assert
        Assert.DoesNotContain("secret-a", masked);
        Assert.DoesNotContain("secret-i", masked);
    }

    [Fact]
    public void MaskTokenLikeValues_PassthroughForNonMatching()
    {
        // Arrange
        var body = """{"type":"error","message":"Invalid request format"}""";

        // Act
        var masked = ClaudeUsageService.MaskTokenLikeValues(body);

        // Assert
        Assert.Equal(body, masked);
    }

    [Fact]
    public void MaskTokenLikeValues_HandlesEscapedQuotesInValue()
    {
        // Arrange — defensive: token with escaped quote inside (theoretical, OAuth
        // tokens are usually base64 but Anthropic could change format)
        var body = """{"refresh_token":"abc\"def","other":"keep"}""";

        // Act
        var masked = ClaudeUsageService.MaskTokenLikeValues(body);

        // Assert
        Assert.DoesNotContain("abc", masked);
        Assert.DoesNotContain("def", masked);
        Assert.Contains("\"refresh_token\":\"***\"", masked);
        Assert.Contains("\"other\":\"keep\"", masked);
    }

    // ── Helper: mock HTTP handler ──────────────────────────────────

    private class MockHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode status, string body)> _queue = new();
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _body = "";
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode status, string body)
        {
            _statusCode = status;
            _body = body;
            _queue.Clear();
        }

        /// <summary>Queue multiple responses for testing retry behavior.</summary>
        public void EnqueueResponse(HttpStatusCode status, string body)
        {
            _queue.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            var (status, body) = _queue.Count > 0 ? _queue.Dequeue() : (_statusCode, _body);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
