using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Auth;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Auth;
using Ziwa.Infrastructure.IO;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase5SupabaseAuthAndLicensingTests
{
    [Fact]
    public async Task RefreshTokenRace_ConcurrentChecksShareSingleRotationAndNeverEvictValidSession()
    {
        string tempRoot = CreateTempDir("ziwa-p501-race-");
        try
        {
            var localStore = new AtomicLocalAuthSessionStore(new ProjectStorageOptions(tempRoot), new AtomicFileWriter());
            var cloudStore = new CloudAuthSessionStore();

            string currentAccessToken = "access-expired-1";
            string validRefreshToken = "refresh-gen-1";
            int refreshCallCount = 0;
            int profileCallsBeforeRotation = 0;
            object gate = new();

            using var handler = new DelegateHttpHandler(async (req, bodyText, ct) =>
            {
                await Task.Yield();
                string pathAndQuery = req.RequestUri!.PathAndQuery;

                if (pathAndQuery.StartsWith("/rest/v1/profiles", StringComparison.Ordinal))
                {
                    string? auth = req.Headers.Authorization?.Parameter;
                    lock (gate)
                    {
                        if (auth == "access-expired-1")
                        {
                            profileCallsBeforeRotation++;
                            return JsonResp(HttpStatusCode.Unauthorized, """{"message":"JWT expired"}""");
                        }
                    }

                    return JsonResp(HttpStatusCode.OK, """[{"plan":"pro","status":"active","license_key":"KEY-1","expires_at":"2027-01-01T00:00:00Z"}]""");
                }

                if (pathAndQuery.StartsWith("/auth/v1/token?grant_type=refresh_token", StringComparison.Ordinal))
                {
                    using JsonDocument doc = JsonDocument.Parse(bodyText);
                    string supplied = doc.RootElement.GetProperty("refresh_token").GetString() ?? string.Empty;

                    await Task.Delay(30, ct);

                    lock (gate)
                    {
                        refreshCallCount++;
                        if (supplied != validRefreshToken)
                        {
                            return JsonResp(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Invalid Refresh Token: Already Used"}""");
                        }

                        validRefreshToken = "refresh-gen-2";
                        currentAccessToken = "access-fresh-2";
                        return JsonResp(HttpStatusCode.OK, """{"access_token":"access-fresh-2","refresh_token":"refresh-gen-2","user":{"id":"user-race-1","email":"race@ziwa.app"}}""");
                    }
                }

                if (pathAndQuery.StartsWith("/rest/v1/rpc/claim_session", StringComparison.Ordinal))
                {
                    return JsonResp(HttpStatusCode.OK, """{"success":true}""");
                }

                return JsonResp(HttpStatusCode.NotFound, "{}");
            });

            var initialProfile = new UserAccountProfile(
                "user-race-1",
                "race@ziwa.app",
                "Race Studio",
                "pro",
                "active",
                DateTimeOffset.UtcNow.AddDays(60),
                "KEY-1",
                DateTimeOffset.UtcNow);
            await localStore.SaveAsync(new PersistedAuthSession(initialProfile, currentAccessToken, validRefreshToken, ActiveSessionId: null));

            var options = new SupabaseAuthOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_key",
                CustomHttpHandler = handler,
            };

            using var client = new SupabaseAuthClient(options, localStore, cloudStore);

            Task<UserAccountProfile>[] concurrent = Enumerable.Range(0, 8)
                .Select(_ => client.CheckSessionStatusAsync())
                .ToArray();
            UserAccountProfile[] results = await Task.WhenAll(concurrent);

            Assert.All(results, r => Assert.Equal("pro", r.Plan));
            Assert.Equal(1, refreshCallCount);
            Assert.Equal(1, profileCallsBeforeRotation);

            PersistedAuthSession? saved = await localStore.LoadAsync();
            Assert.NotNull(saved);
            Assert.Equal("access-fresh-2", saved!.AccessToken);
            Assert.Equal("refresh-gen-2", saved.RefreshToken);
            Assert.False(string.IsNullOrWhiteSpace(saved.ActiveSessionId));
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task LogoutDuringInFlightLogin_PreventsSessionResurrection()
    {
        string tempRoot = CreateTempDir("ziwa-p501-logout-");
        try
        {
            var localStore = new AtomicLocalAuthSessionStore(new ProjectStorageOptions(tempRoot), new AtomicFileWriter());
            var cloudStore = new CloudAuthSessionStore();
            var loginEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLoginTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var handler = new DelegateHttpHandler(async (req, _, ct) =>
            {
                string path = req.RequestUri!.PathAndQuery;
                if (path.StartsWith("/auth/v1/token?grant_type=password", StringComparison.Ordinal))
                {
                    loginEnteredTcs.TrySetResult();
                    await releaseLoginTcs.Task.WaitAsync(ct);
                    return JsonResp(HttpStatusCode.OK, """{"access_token":"late-jwt-token","refresh_token":"late-refresh","user":{"id":"u-late","email":"late@ziwa.app","user_metadata":{"name":"Late User"}}}""");
                }

                if (path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal))
                {
                    return JsonResp(HttpStatusCode.OK, """[{"plan":"pro","status":"active","expires_at":"2027-05-01T00:00:00Z"}]""");
                }

                if (path.StartsWith("/rest/v1/rpc/claim_session", StringComparison.Ordinal) ||
                    path.StartsWith("/auth/v1/logout", StringComparison.Ordinal))
                {
                    return JsonResp(HttpStatusCode.OK, """{"success":true}""");
                }

                return JsonResp(HttpStatusCode.OK, "{}");
            });

            var options = new SupabaseAuthOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_key",
                CustomHttpHandler = handler,
            };

            using var client = new SupabaseAuthClient(options, localStore, cloudStore);

            Task<UserAccountProfile> loginTask = client.LoginWithPasswordAsync("late@ziwa.app", "SecretPass123");
            await loginEnteredTcs.Task;

            // User clicks Logout BEFORE late login HTTP calls finish committing
            await client.LogoutAsync();
            releaseLoginTcs.SetResult();

            AuthException staleEx = await Assert.ThrowsAsync<AuthException>(() => loginTask);
            Assert.Equal(AuthErrorCode.SessionTransitionStale, staleEx.ErrorCode);

            Assert.False(client.IsAuthenticated);
            Assert.Null(cloudStore.GetCurrentSession());
            Assert.Null(await localStore.LoadAsync());
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task RegisterOtpLicenseActivationSingleActiveSessionOfflineGraceAndZeroTokenLogs_WorkEndToEnd()
    {
        string tempRoot = CreateTempDir("ziwa-p501-e2e-");
        try
        {
            var localStore = new AtomicLocalAuthSessionStore(new ProjectStorageOptions(tempRoot), new AtomicFileWriter());
            var cloudStore = new CloudAuthSessionStore();

            bool sessionSuperseded = false;
            bool simulateNetworkOutage = false;
            string currentPlan = "free";
            string currentStatus = "none";

            const string jwtMarker = "jwt_secret_marker_991";
            const string refreshMarker = "refresh_secret_marker_992";

            using var handler = new DelegateHttpHandler((req, _, _) =>
            {
                if (simulateNetworkOutage)
                {
                    throw new HttpRequestException("Simulated temporary network outage");
                }

                string path = req.RequestUri!.PathAndQuery;
                if (path.StartsWith("/auth/v1/signup", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"id":"u-flow-1","email":"flow@ziwa.app"}"""));
                }

                if (path.StartsWith("/auth/v1/verify", StringComparison.Ordinal))
                {
                    string verifyPayload = JsonSerializer.Serialize(new
                    {
                        access_token = jwtMarker,
                        refresh_token = refreshMarker,
                        user = new
                        {
                            id = "u-flow-1",
                            email = "flow@ziwa.app",
                            user_metadata = new { name = "Flow Studio" },
                        },
                    });
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, verifyPayload));
                }

                if (path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, $$"""[{"plan":"{{currentPlan}}","status":"{{currentStatus}}","license_key":"KEY-PRO","expires_at":"2027-12-31T00:00:00Z"}]"""));
                }

                if (path.StartsWith("/rest/v1/rpc/claim_session", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"success":true}"""));
                }

                if (path.StartsWith("/rest/v1/rpc/activate_license", StringComparison.Ordinal))
                {
                    currentPlan = "pro";
                    currentStatus = "active";
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"plan":"pro","status":"active","expires_at":"2027-12-31T00:00:00Z"}"""));
                }

                if (path.StartsWith("/rest/v1/rpc/check_session", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(
                        HttpStatusCode.OK,
                        sessionSuperseded
                            ? """{"success":true,"active":false,"superseded":true}"""
                            : """{"success":true,"active":true,"superseded":false}"""));
                }

                return Task.FromResult(JsonResp(HttpStatusCode.Unauthorized, """{"msg":"Invalid login credentials"}"""));
            });

            var options = new SupabaseAuthOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_key",
                CustomHttpHandler = handler,
            };

            using var client = new SupabaseAuthClient(options, localStore, cloudStore);

            // 1. Signup pending OTP
            UserAccountProfile pending = await client.RegisterAsync("Flow Studio", "flow@ziwa.app", "StrongPass1");
            Assert.True(pending.IsPendingOtp);
            Assert.False(client.IsAuthenticated);

            // 2. Verify OTP -> active session + claim_session
            UserAccountProfile verified = await client.VerifySignupOtpAsync("flow@ziwa.app", "123456");
            Assert.True(verified.IsAuthenticated);
            Assert.Equal("free", verified.Plan);

            // 3. Activate License Key -> Pro plan synced to local & cloud stores
            UserAccountProfile licensed = await client.ActivateLicenseKeyAsync("ZIWA-PRO-KEY-2026");
            Assert.Equal("pro", licensed.Plan);
            Assert.Equal("pro", cloudStore.GetCurrentSession()?.Plan);

            // 4. Offline Grace retains cached Pro session during temporary network outage
            simulateNetworkOutage = true;
            UserAccountProfile offlineKept = await client.CheckSessionStatusAsync();
            Assert.True(offlineKept.IsAuthenticated);
            Assert.Equal("pro", offlineKept.Plan);
            simulateNetworkOutage = false;

            // 5. Last-Wins supersession evicts local session with ERR_SESSION_SUPERSEDED marker & zero leaked tokens
            sessionSuperseded = true;
            AuthException evictedEx = await Assert.ThrowsAsync<AuthException>(() => client.CheckSessionStatusAsync());
            Assert.Equal(AuthErrorCode.SessionSuperseded, evictedEx.ErrorCode);
            Assert.True(evictedEx.IsSessionSuperseded);
            Assert.DoesNotContain(jwtMarker, evictedEx.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(refreshMarker, evictedEx.ToString(), StringComparison.Ordinal);
            Assert.False(client.IsAuthenticated);
            Assert.Null(cloudStore.GetCurrentSession());
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task GoogleOAuthLoopback_EnforcesOriginStateAndSingleExchangeGuards()
    {
        string tempRoot = CreateTempDir("ziwa-p501-oauth-");
        try
        {
            var localStore = new AtomicLocalAuthSessionStore(new ProjectStorageOptions(tempRoot), new AtomicFileWriter());
            var cloudStore = new CloudAuthSessionStore();

            // Keep the login in flight until the test has checked the receiver's response.
            var userResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new DelegateHttpHandler((req, _, ct) =>
            {
                string path = req.RequestUri!.PathAndQuery;
                if (path.StartsWith("/auth/v1/user", StringComparison.Ordinal))
                {
                    return userResponse.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }

                if (path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """[{"plan":"enterprise","status":"active","expires_at":"2027-12-31T00:00:00Z"}]"""));
                }

                if (path.StartsWith("/rest/v1/rpc/claim_session", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"success":true}"""));
                }

                return Task.FromResult(JsonResp(HttpStatusCode.NotFound, "{}"));
            });

            Uri? capturedAuthUri = null;
            var launcherReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var browser = new DelegateBrowserLauncher(uri =>
            {
                capturedAuthUri = uri;
                launcherReady.TrySetResult();
            });

            var options = new SupabaseAuthOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_key",
                CustomHttpHandler = handler,
            };

            using var client = new SupabaseAuthClient(options, localStore, cloudStore, browser);
            Task<UserAccountProfile> oauthTask = client.LoginWithGoogleAsync();
            await launcherReady.Task;

            Assert.NotNull(capturedAuthUri);
            Assert.DoesNotContain("&state=", capturedAuthUri!.Query, StringComparison.OrdinalIgnoreCase);

            // Extract loopback callback URL from redirect_to
            string redirectParam = System.Web.HttpUtility.ParseQueryString(capturedAuthUri.Query)["redirect_to"]!;
            var callbackUri = new Uri(redirectParam);
            string baseLoopback = $"{callbackUri.Scheme}://{callbackUri.Authority}";

            using var http = new HttpClient();
            string callbackHtml = await http.GetStringAsync(callbackUri);
            string marker = "encodeURIComponent('";
            int start = callbackHtml.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            int end = callbackHtml.IndexOf('\'', start);
            string extractedState = callbackHtml[start..end];

            // Untrusted Origin -> 403 Forbidden
            using (var badOriginReq = new HttpRequestMessage(HttpMethod.Post, $"{baseLoopback}/exchange"))
            {
                badOriginReq.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                badOriginReq.Content = new StringContent($"access_token=abc&state={extractedState}", Encoding.UTF8, "application/x-www-form-urlencoded");
                using HttpResponseMessage resp = await http.SendAsync(badOriginReq);
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }

            // Invalid state -> 403 Forbidden
            using (var badStateReq = new HttpRequestMessage(HttpMethod.Post, $"{baseLoopback}/exchange"))
            {
                badStateReq.Headers.TryAddWithoutValidation("Origin", baseLoopback);
                badStateReq.Content = new StringContent("access_token=abc&state=wrongstate", Encoding.UTF8, "application/x-www-form-urlencoded");
                using HttpResponseMessage resp = await http.SendAsync(badStateReq);
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }

            // Valid first exchange -> 200 OK
            using (var validReq = new HttpRequestMessage(HttpMethod.Post, $"{baseLoopback}/exchange"))
            {
                validReq.Headers.TryAddWithoutValidation("Origin", baseLoopback);
                validReq.Content = new StringContent(
                    $"access_token=oauth-jwt-ok&refresh_token=oauth-ref-ok&state={Uri.EscapeDataString(extractedState)}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");
                using HttpResponseMessage resp = await http.SendAsync(validReq);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }

            // Duplicate replay -> 409 Conflict
            using (var replayReq = new HttpRequestMessage(HttpMethod.Post, $"{baseLoopback}/exchange"))
            {
                replayReq.Headers.TryAddWithoutValidation("Origin", baseLoopback);
                replayReq.Content = new StringContent(
                    $"access_token=oauth-jwt-ok&state={Uri.EscapeDataString(extractedState)}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");
                using HttpResponseMessage resp = await http.SendAsync(replayReq);
                Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            }

            userResponse.SetResult(JsonResp(HttpStatusCode.OK, """{"id":"u-oauth-1","email":"google@ziwa.app","user_metadata":{"full_name":"Google Studio"}}"""));
            UserAccountProfile profile = await oauthTask;
            Assert.Equal("Google Studio", profile.DisplayName);
            Assert.Equal("enterprise", profile.Plan);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task AtomicLocalAuthSessionStore_DowngradesTamperedLicenseWithoutValidSignature()
    {
        string tempRoot = CreateTempDir("ziwa-p501-sig-");
        try
        {
            var store = new AtomicLocalAuthSessionStore(new ProjectStorageOptions(tempRoot), new AtomicFileWriter());
            var profile = new UserAccountProfile(
                "u-sig-1",
                "sig@ziwa.app",
                "Sig User",
                "pro",
                "active",
                DateTimeOffset.UtcNow.AddDays(30),
                "ZIWA-KEY",
                DateTimeOffset.UtcNow);
            await store.SaveAsync(new PersistedAuthSession(profile, "jwt-1", "ref-1", "sid-1"));

            string sigPath = Path.Combine(tempRoot, "Auth", "license.sig");
            Assert.True(File.Exists(sigPath));
            await File.WriteAllTextAsync(sigPath, "tampered-signature");

            PersistedAuthSession? loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("free", loaded!.Profile.Plan);
            Assert.Equal("none", loaded.Profile.Status);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    private static HttpResponseMessage JsonResp(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string CreateTempDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class DelegateBrowserLauncher : IBrowserLauncher
    {
        private readonly Action<Uri> _onOpen;

        public DelegateBrowserLauncher(Action<Uri> onOpen)
        {
            _onOpen = onOpen;
        }

        public Task OpenUrlAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            _onOpen(uri);
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _fn;

        public DelegateHttpHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> fn)
        {
            _fn = fn;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            return await _fn(request, body, cancellationToken);
        }
    }
}
