using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public class ClaudeUsageService : IDisposable
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    // Production OAuth client_id used by Claude Code (extracted from cli binary).
    // Previously we used a URL placeholder which caused HTTP 400 "Invalid request format".
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string CredentialsFileName = ".credentials.json";
    private const string DefaultScopes = "user:inference user:profile user:sessions:claude_code user:mcp_servers user:file_upload";

    // Refresh the token 5 minutes before it actually expires to avoid edge cases
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    // Adaptive cache: we don't need live numbers when you're at 10% utilization,
    // so refresh less often then and conserve requests to a throttle-prone endpoint.
    // Kicks in more frequently as you approach the limit.
    private static readonly TimeSpan CacheLow = TimeSpan.FromMinutes(10);  // utilization < 70%
    private static readonly TimeSpan CacheMid = TimeSpan.FromMinutes(5);   //               70-90%
    private static readonly TimeSpan CacheHigh = TimeSpan.FromMinutes(3);  //               >= 90%
    private static readonly TimeSpan CacheFirst = TimeSpan.FromMinutes(3); // no data yet

    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitBackoffMax = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan AuthFailBackoff = TimeSpan.FromMinutes(2);
    private const int MaxConsecutiveFailures = 5;

    private readonly HttpClient _httpClient;
    private FileSystemWatcher? _credentialsWatcher;
    private long _lastCredentialsEventTicks; // accessed via Interlocked from FSW callback
    private static readonly long CredentialsDebounceTicks = TimeSpan.FromMilliseconds(500).Ticks;
    private UsageData? _cachedData;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private string? _lastError;
    private DateTime _backoffUntil = DateTime.MinValue;
    private bool _inFlight;
    private int _consecutiveFailures;
    private bool _autoRetryDisabled;
    private ApiErrorCategory _lastErrorCategory = ApiErrorCategory.None;

    public UsageData? CachedData => _cachedData;
    public string? LastError => _lastError;
    public DateTime LastFetchTime => _lastFetchTime;
    public bool IsBackedOff => DateTime.UtcNow < _backoffUntil;
    public TimeSpan BackoffRemaining => IsBackedOff ? _backoffUntil - DateTime.UtcNow : TimeSpan.Zero;
    public bool AutoRetryDisabled => _autoRetryDisabled;
    public int ConsecutiveFailures => _consecutiveFailures;
    public ApiErrorCategory LastErrorCategory => _lastErrorCategory;

    /// <summary>
    /// Replaces values of "refresh_token", "access_token", "id_token" JSON fields
    /// with "***" so they don't leak into log files via error response bodies.
    /// Handles escaped quotes inside the value (e.g. \" — defensive, OAuth tokens
    /// are typically alphanumeric base64 but Anthropic could change format).
    /// </summary>
    internal static string MaskTokenLikeValues(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        return System.Text.RegularExpressions.Regex.Replace(
            body,
            "\"(refresh_token|access_token|id_token)\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"",
            "\"$1\":\"***\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Fired when credentials.json changes (typically after Claude Code refreshes
    /// the OAuth token). Subscribers should trigger an immediate fetch.
    /// Fires on a thread pool thread — use Dispatcher.BeginInvoke before mutating UI state.
    /// </summary>
    public event Action? CredentialsRefreshed;

    /// <summary>
    /// Clears backoff and auto-retry-disabled state. Must be called from the UI thread
    /// to avoid races with FetchUsageAsync (which is also UI-thread-bound).
    /// Call this from a CredentialsRefreshed handler before triggering a fetch.
    /// </summary>
    public void ClearBackoffOnCredentialsRefresh()
    {
        if (_autoRetryDisabled || IsBackedOff)
        {
            AppLogger.Info("Clearing backoff (token likely refreshed by Claude Code)");
            _backoffUntil = DateTime.MinValue;
            _autoRetryDisabled = false;
            _consecutiveFailures = 0;
            _lastError = null;
            _lastErrorCategory = ApiErrorCategory.None;
        }
    }

    /// <summary>
    /// Debug helper: drops the cached usage data and any backoff state so the next
    /// fetch will go all the way to the API. Useful when diagnosing "stuck at old
    /// values" complaints.
    /// </summary>
    public void ClearCache()
    {
        _cachedData = null;
        _lastFetchTime = DateTime.MinValue;
        _lastError = null;
        _backoffUntil = DateTime.MinValue;
        _consecutiveFailures = 0;
        _autoRetryDisabled = false;
        AppLogger.Info("Usage cache cleared");
    }

    /// <summary>
    /// Debug helper: snapshot the service's internal state so it can be dumped to
    /// the log for bug reports.
    /// </summary>
    public string GetStateSnapshot()
    {
        var hasData = _cachedData != null;
        var lastFetch = _lastFetchTime == DateTime.MinValue ? "never" : _lastFetchTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var cacheAge = _lastFetchTime == DateTime.MinValue
            ? "n/a"
            : $"{(DateTime.UtcNow - _lastFetchTime).TotalSeconds:F0}s";
        var backoff = IsBackedOff ? $"yes, {BackoffRemaining.TotalMinutes:F1}m remaining" : "no";
        var interval = $"{CurrentCacheInterval.TotalMinutes:F0}m";

        var utilLine = "n/a";
        if (_cachedData != null)
        {
            var parts = new List<string>();
            if (_cachedData.FiveHour != null) parts.Add($"5h={_cachedData.FiveHour.Utilization:P0}");
            if (_cachedData.SevenDay != null) parts.Add($"weekly={_cachedData.SevenDay.Utilization:P0}");
            if (_cachedData.SevenDayOpus != null) parts.Add($"opus={_cachedData.SevenDayOpus.Utilization:P0}");
            if (parts.Count > 0) utilLine = string.Join(", ", parts);
        }

        return
            $"  HasCachedData: {hasData}\n" +
            $"  LastFetch: {lastFetch}  (age: {cacheAge})\n" +
            $"  CurrentCacheInterval: {interval}\n" +
            $"  Utilization: {utilLine}\n" +
            $"  LastError: {_lastError ?? "(none)"}\n" +
            $"  IsBackedOff: {backoff}\n" +
            $"  ConsecutiveFailures: {_consecutiveFailures}\n" +
            $"  AutoRetryDisabled: {_autoRetryDisabled}\n" +
            $"  InFlight: {_inFlight}";
    }

    public ClaudeUsageService() : this(new HttpClientHandler())
    {
    }

    /// <summary>
    /// Constructor for testing: accepts an HttpMessageHandler so tests can inject
    /// a mock handler without hitting the network.
    /// </summary>
    internal ClaudeUsageService(HttpMessageHandler handler, bool watchCredentials = true)
    {
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        if (watchCredentials)
        {
            StartWatchingCredentials();
        }
    }

    /// <summary>
    /// Watches ~/.claude/.credentials.json for changes. When Claude Code refreshes
    /// the OAuth token, the file is rewritten. We detect this and clear our backoff
    /// + auto-retry-disabled state so the next timer tick picks up the fresh token
    /// automatically — no manual Refresh click needed.
    /// </summary>
    private void StartWatchingCredentials()
    {
        try
        {
            var claudeDir = ClaudeConfigService.ClaudeHomeDir;

            if (!Directory.Exists(claudeDir))
            {
                return;
            }

            // Clean up stale .credentials.json.bak from older widget versions —
            // contained a copy of the previous refresh_token, security concern.
            var stalebak = Path.Combine(claudeDir, CredentialsFileName + ".bak");
            if (File.Exists(stalebak))
            {
                try
                {
                    File.Delete(stalebak);
                    AppLogger.Info("Removed stale .credentials.json.bak from older widget version");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to remove stale .bak: {ex.Message}");
                }
            }

            _credentialsWatcher = new FileSystemWatcher(claudeDir, CredentialsFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _credentialsWatcher.Changed += (_, _) =>
            {
                // FileSystemWatcher fires 2-3 times per file write on Windows.
                // Debounce to avoid spamming subscribers. Use Interlocked for atomic
                // read/write since this runs on a thread pool thread.
                var nowTicks = DateTime.UtcNow.Ticks;
                var prevTicks = Interlocked.Exchange(ref _lastCredentialsEventTicks, nowTicks);
                if (nowTicks - prevTicks < CredentialsDebounceTicks)
                {
                    return;
                }

                AppLogger.Info($"Credentials file changed (autoRetryDisabled={_autoRetryDisabled}, backedOff={IsBackedOff})");

                // Notify subscribers on the UI thread — they own state mutation
                // (HandleCredentialsRefreshed) to avoid races with FetchUsageAsync.
                try
                {
                    CredentialsRefreshed?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"CredentialsRefreshed handler failed: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to watch credentials file: {ex.Message}");
        }
    }

    internal void RegisterFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _autoRetryDisabled = true;
        }
    }

    /// <summary>
    /// How long the current cached value is considered fresh. Adaptive based on the
    /// highest window utilization - low usage users get slower refresh so we don't
    /// spam the Anthropic throttle-prone /oauth/usage endpoint.
    /// </summary>
    public TimeSpan CurrentCacheInterval
    {
        get
        {
            if (_cachedData == null)
            {
                return CacheFirst;
            }

            var maxUtil = 0.0;
            if (_cachedData.FiveHour != null) maxUtil = Math.Max(maxUtil, _cachedData.FiveHour.Utilization);
            if (_cachedData.SevenDay != null) maxUtil = Math.Max(maxUtil, _cachedData.SevenDay.Utilization);
            if (_cachedData.SevenDayOpus != null) maxUtil = Math.Max(maxUtil, _cachedData.SevenDayOpus.Utilization);

            // Utilization is 0..1 (0.9 = 90%)
            if (maxUtil >= 0.90) return CacheHigh;
            if (maxUtil >= 0.70) return CacheMid;
            return CacheLow;
        }
    }

    /// <summary>
    /// Fetches usage data from the Anthropic API.
    /// <paramref name="forceRefresh"/>: skip cache freshness check (but still respect backoff/auto-retry).
    /// <paramref name="manualRefresh"/>: user explicitly clicked Refresh — bypass EVERYTHING
    /// (backoff + auto-retry-disabled). Only pass true for user-initiated actions.
    /// </summary>
    public async Task<UsageData?> FetchUsageAsync(bool forceRefresh = false, bool manualRefresh = false)
    {
        if (!forceRefresh && !manualRefresh && _cachedData != null && DateTime.UtcNow - _lastFetchTime < CurrentCacheInterval)
        {
            return _cachedData;
        }

        // Manual refresh is user-initiated (Refresh button click / Debug clear cache).
        // It bypasses both backoff and auto-retry-disabled so the user always has
        // an escape hatch from stuck states (e.g. expired token overnight).
        if (manualRefresh)
        {
            _backoffUntil = DateTime.MinValue;
            _autoRetryDisabled = false;
            _consecutiveFailures = 0;
            AppLogger.Info("Manual refresh requested - bypassing backoff, resetting failure counter");
        }
        else
        {
            // Auto-retry has been disabled after too many consecutive failures.
            // Wait for a manual Refresh to re-enable.
            if (_autoRetryDisabled)
            {
                return _cachedData;
            }

            // Respect backoff window after 401/429 - do not hammer the API
            if (IsBackedOff)
            {
                return _cachedData;
            }
        }

        // Re-entrancy guard: if a previous call is still awaiting the network,
        // don't start a second one (would race on _backoffUntil/_lastError/_cachedData).
        // Called from UI thread, so a plain bool check is sufficient.
        if (_inFlight)
        {
            if (manualRefresh)
            {
                AppLogger.Info("Manual refresh dropped - fetch already in flight");
            }
            return _cachedData;
        }
        _inFlight = true;

        try
        {
        // Retry up to 2 times with short delay on transient failures
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var token = await ReadAccessTokenAsync();
                if (string.IsNullOrEmpty(token))
                {
                    _lastErrorCategory = ApiErrorCategory.Auth;
                    _lastError = "No access token found. Is Claude Code logged in?";
                    AppLogger.Warn("No access token in credentials.json");
                    return _cachedData;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                request.Headers.Add("Authorization", $"Bearer {token}");
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var diag = await GetResponseDiagnostics(response);
                    // Honor Retry-After if present, otherwise use default backoff
                    var retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date.HasValue == true
                            ? response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow
                            : (TimeSpan?)null)
                        ?? RateLimitBackoff;
                    // Negative or zero (e.g. stale Retry-After date, clock skew) → use default
                    if (retryAfter <= TimeSpan.Zero)
                    {
                        retryAfter = RateLimitBackoff;
                    }
                    if (retryAfter < TimeSpan.FromSeconds(30))
                    {
                        retryAfter = TimeSpan.FromSeconds(30);
                    }

                    RegisterFailure();

                    // Exponential escalation on consecutive failures so we don't spin
                    // at the same interval forever. Progression with 5 min base:
                    // 5 / 10 / 20 / 40 / 60 (capped). Floor: server's Retry-After.
                    // RegisterFailure was called first so _consecutiveFailures >= 1;
                    // subtract 1 so the first failure uses 2^0 = 5 min.
                    var exp = Math.Min(_consecutiveFailures - 1, 4);
                    var escalated = TimeSpan.FromSeconds(RateLimitBackoff.TotalSeconds * Math.Pow(2, exp));
                    if (escalated > retryAfter)
                    {
                        retryAfter = escalated;
                    }
                    if (retryAfter > RateLimitBackoffMax)
                    {
                        retryAfter = RateLimitBackoffMax;
                    }
                    _backoffUntil = DateTime.UtcNow + retryAfter;
                    _lastErrorCategory = ApiErrorCategory.RateLimit;
                    _lastError = _autoRetryDisabled
                        ? "Auto-retry stopped after repeated failures - click Refresh"
                        : "API throttled - showing last known values";
                    AppLogger.Warn($"API rate limited (429) - backing off for {retryAfter.TotalSeconds:F0}s (consecutive failures: {_consecutiveFailures}). {diag}");
                    return _cachedData;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var diag = await GetResponseDiagnostics(response);
                    RegisterFailure();
                    _backoffUntil = DateTime.UtcNow + AuthFailBackoff;
                    _lastErrorCategory = ApiErrorCategory.Auth;
                    _lastError = _autoRetryDisabled
                        ? "Auto-retry stopped after repeated failures - click Refresh"
                        : "Token expired - auto-refresh will be attempted";
                    AppLogger.Warn($"API auth failed (401) - backing off for {AuthFailBackoff.TotalMinutes:F0}m (consecutive failures: {_consecutiveFailures}). {diag}");
                    return _cachedData;
                }

                // Log any unexpected non-2xx status with full details
                if (!response.IsSuccessStatusCode)
                {
                    var diag = await GetResponseDiagnostics(response);
                    RegisterFailure();
                    _lastErrorCategory = ApiErrorCategory.Http;
                    _lastError = $"HTTP {(int)response.StatusCode}";
                    AppLogger.Warn($"API unexpected status: {diag} (consecutive failures: {_consecutiveFailures})");
                    return _cachedData;
                }

                var json = await response.Content.ReadAsStringAsync();
                UsageData? data;
                try
                {
                    data = JsonSerializer.Deserialize<UsageData>(json);
                }
                catch (JsonException jex)
                {
                    // Log raw response + content-type so we can see what changed.
                    var contentType = response.Content.Headers.ContentType?.ToString() ?? "unknown";
                    var truncated = json.Length > 500 ? json[..500] + "..." : json;
                    AppLogger.Error($"Usage API JSON parse failed. Content-Type: {contentType}. Raw response (truncated): {truncated}", jex);
                    RegisterFailure();
                    _backoffUntil = DateTime.UtcNow + RateLimitBackoff;
                    _lastErrorCategory = ApiErrorCategory.ParseError;
                    _lastError = _autoRetryDisabled
                        ? "Auto-retry stopped after repeated failures - click Refresh"
                        : "Response format changed - see app.log";
                    return _cachedData;
                }

                if (data != null)
                {
                    var wasRecovery = _consecutiveFailures > 0;
                    _cachedData = data;
                    _lastFetchTime = DateTime.UtcNow;
                    _lastError = null;
                    _lastErrorCategory = ApiErrorCategory.None;
                    _backoffUntil = DateTime.MinValue;
                    _consecutiveFailures = 0;
                    _autoRetryDisabled = false;

                    if (wasRecovery || attempt > 1)
                    {
                        // Log response shape on recovery so we have a baseline for
                        // future comparison when the format changes again.
                        var truncated = json.Length > 300 ? json[..300] + "..." : json;
                        AppLogger.Info($"Usage API fetch succeeded (recovery/retry). Response sample: {truncated}");
                    }
                }

                return _cachedData;
            }
            catch (HttpRequestException ex)
            {
                _lastErrorCategory = ApiErrorCategory.Network;
                _lastError = $"HTTP error: {ex.Message}";
                AppLogger.Warn($"Usage API HTTP error (attempt {attempt}): {ex.Message}");
                if (attempt < 2)
                {
                    await Task.Delay(1000);
                    continue;
                }
                RegisterFailure();
            }
            catch (TaskCanceledException)
            {
                _lastErrorCategory = ApiErrorCategory.Network;
                _lastError = "Request timed out";
                AppLogger.Warn($"Usage API timeout (attempt {attempt})");
                if (attempt < 2)
                {
                    await Task.Delay(1000);
                    continue;
                }
                RegisterFailure();
            }
            catch (Exception ex)
            {
                _lastErrorCategory = ApiErrorCategory.Other;
                _lastError = $"Error: {ex.Message}";
                AppLogger.Error("Usage API unexpected error", ex);
                RegisterFailure();
                break;
            }
        }

        return _cachedData;
        }
        finally
        {
            _inFlight = false;
        }
    }

    /// <summary>Override point for tests to inject a fake token.</summary>
    internal Func<string?>? AccessTokenOverride { get; set; }

    private async Task<string?> ReadAccessTokenAsync()
    {
        if (AccessTokenOverride != null)
        {
            return AccessTokenOverride();
        }

        var credentialsPath = Path.Combine(
            ClaudeConfigService.ClaudeHomeDir, CredentialsFileName);

        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(credentialsPath);
        var credentials = JsonSerializer.Deserialize<CredentialsData>(json);

        var oauth = credentials?.ClaudeAiOauth;
        if (oauth?.RefreshToken != null && oauth.ExpiresAt > 0)
        {
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt);
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining < TokenRefreshMargin)
            {
                AppLogger.Info($"Token expires in {remaining.TotalMinutes:F1}m (threshold: {TokenRefreshMargin.TotalMinutes}m) — attempting refresh");
                var refreshed = await TryRefreshTokenAsync(oauth.RefreshToken, credentialsPath, oauth.Scopes);
                if (refreshed != null)
                {
                    return refreshed;
                }
            }
        }

        return oauth?.AccessToken;
    }

    /// <summary>
    /// Attempts to refresh the OAuth token using the refresh_token grant.
    /// On success, writes the new credentials to disk and returns the new access token.
    /// On failure, logs the error and returns null (caller falls back to existing token).
    /// </summary>
    private async Task<string?> TryRefreshTokenAsync(string refreshToken, string credentialsPath, List<string>? scopes)
    {
        try
        {
            // Claude Code sends JSON (not form-urlencoded) with scope parameter.
            // Reverse-engineered from cli.js ll6() function.
            // IMPORTANT: Content-Type must be exactly "application/json" without charset=utf-8
            var scopeString = scopes?.Count > 0
                ? string.Join(" ", scopes)
                : DefaultScopes;

            var payload = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
                ["scope"] = scopeString
            };

            var jsonBody = payload.ToJsonString();
            var content = new StringContent(jsonBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            AppLogger.Info($"Token refresh attempt: endpoint={TokenEndpoint}, scopes={scopeString}, tokenLength={refreshToken.Length}");

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                // Defensive: strip any token values from logged body in case Anthropic
                // ever echoes the input in an error response.
                var safe = MaskTokenLikeValues(body);
                var truncated = safe.Length > 500 ? safe[..500] + "..." : safe;
                AppLogger.Warn($"Token refresh failed: HTTP {(int)response.StatusCode}. Body: {truncated}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var newAccessToken = root.GetProperty("access_token").GetString();
            var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600;
            var newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();

            if (string.IsNullOrEmpty(newAccessToken))
            {
                AppLogger.Warn("Token refresh returned empty access_token");
                return null;
            }

            // Update credentials file — preserve all existing fields via JsonNode round-trip.
            // Note: there is an inherent race with Claude Code writing this file.
            // We re-read immediately before writing to minimize the window. In the worst case,
            // Claude Code's concurrent write is lost but it will re-refresh on its next cycle.
            var existingJson = await File.ReadAllTextAsync(credentialsPath);
            var node = JsonNode.Parse(existingJson);
            if (node?["claudeAiOauth"] is JsonObject oauthNode)
            {
                oauthNode["accessToken"] = newAccessToken;
                oauthNode["refreshToken"] = newRefreshToken;
                oauthNode["expiresAt"] = newExpiresAt;
            }

            var updatedJson = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (updatedJson != null)
            {
                // Atomic write (temp + move). Skip .bak backup — would persist old
                // refresh_token on disk indefinitely, which is a security concern.
                ClaudeConfigService.SaveText(credentialsPath, updatedJson, skipBackup: true);
            }

            // Clear any backoff state since we now have a fresh token
            _backoffUntil = DateTime.MinValue;
            _autoRetryDisabled = false;
            _consecutiveFailures = 0;
            _lastError = null;

            AppLogger.Info($"OAuth token refreshed successfully (expires in {expiresIn}s)");
            return newAccessToken;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Token refresh failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts key diagnostic info from an HTTP response for structured logging.
    /// Truncates the body to avoid flooding the log.
    /// </summary>
    private static async Task<string> GetResponseDiagnostics(HttpResponseMessage response, int maxBodyChars = 500)
    {
        var parts = new List<string>
        {
            $"Status: {(int)response.StatusCode} {response.ReasonPhrase}",
            $"Content-Type: {response.Content.Headers.ContentType}",
        };

        if (response.Headers.RetryAfter != null)
        {
            parts.Add($"Retry-After: {response.Headers.RetryAfter}");
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(body))
            {
                var truncated = body.Length > maxBodyChars ? body[..maxBodyChars] + "..." : body;
                parts.Add($"Body: {truncated}");
            }
        }
        catch
        {
            parts.Add("Body: (failed to read)");
        }

        return string.Join(" | ", parts);
    }

    public void Dispose()
    {
        _credentialsWatcher?.Dispose();
        _httpClient.Dispose();
    }
}

/// <summary>
/// Category of the most recent API error. Used by UI to show the right
/// help message when AutoRetryDisabled hides the original error text.
/// </summary>
public enum ApiErrorCategory
{
    None,
    Auth,         // 401, missing token, expired
    RateLimit,    // 429
    Network,      // HttpRequestException, timeout
    Http,         // unexpected non-2xx
    ParseError,   // JSON deserialize failed
    Other,
}
