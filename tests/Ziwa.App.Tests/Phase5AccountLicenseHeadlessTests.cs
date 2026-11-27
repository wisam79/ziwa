using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;
using Ziwa.Application.Persistence;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Auth;

namespace Ziwa.App.Tests;

public sealed class Phase5AccountLicenseHeadlessTests
{
    [AvaloniaFact]
    public async Task AccountLicenseDialog_SupportsLoginLicenseActivationQuotaSyncAndAccessibility()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p501-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string currentPlan = "free";
            string currentStatus = "none";

            using var handler = new DelegateHttpHandler((req, _, _) =>
            {
                string path = req.RequestUri!.PathAndQuery;
                if (path.StartsWith("/auth/v1/token?grant_type=password", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"access_token":"ui-jwt-1","refresh_token":"ui-ref-1","user":{"id":"11111111-2222-4333-8444-555555555555","email":"studio@ziwa.app","user_metadata":{"name":"اسم استوديو عربي طويل جدًا لمعاينة شارة الحساب في مساحة العمل"}}}"""));
                }

                if (path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, $$"""[{"plan":"{{currentPlan}}","status":"{{currentStatus}}","license_key":"ZIWA-PRO-1","expires_at":"2027-10-01T00:00:00Z"}]"""));
                }

                if (path.StartsWith("/rest/v1/rpc/claim_session", StringComparison.Ordinal) ||
                    path.StartsWith("/auth/v1/logout", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"success":true}"""));
                }

                if (path.StartsWith("/rest/v1/rpc/activate_license", StringComparison.Ordinal))
                {
                    currentPlan = "pro";
                    currentStatus = "active";
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, """{"plan":"pro","status":"active","expires_at":"2027-10-01T00:00:00Z"}"""));
                }

                if (path.StartsWith("/rest/v1/rpc/check_and_record_ai_usage", StringComparison.Ordinal))
                {
                    int limit = currentPlan == "pro" ? 15 : 5;
                    return Task.FromResult(JsonResp(HttpStatusCode.OK, $$"""{"success":true,"used_today":2,"daily_limit":{{limit}}}"""));
                }

                return Task.FromResult(JsonResp(HttpStatusCode.NotFound, "{}"));
            });

            var authOptions = new SupabaseAuthOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_ui",
                CustomHttpHandler = handler,
            };
            var cloudOptions = new CloudAiOptions
            {
                SupabaseUrl = SupabaseAuthOptions.DefaultSupabaseUrl,
                SupabasePublishableKey = "sb_publishable_test_ui",
                CustomHttpHandler = handler,
            };

            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot),
                cloudAiOptions: cloudOptions,
                authOptions: authOptions);

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
            window.Show();

            AccountLicenseView dialog = Assert.Single(window.GetVisualDescendants().OfType<AccountLicenseView>());
            Assert.Equal(FlowDirection.RightToLeft, dialog.FlowDirection);
            foreach (Button btn in dialog.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)), $"Missing AutomationProperties.Name on '{btn.Name}'.");
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            vm.OpenAccountDialogCommand.Execute(null);
            Assert.True(vm.IsAccountDialogOpen);

            vm.AccountEmailInput = "studio@ziwa.app";
            vm.AccountPasswordInput = "ZiwaPass123";
            await vm.SubmitLoginCommand.ExecuteAsync(null);

            Assert.True(vm.IsUserAuthenticated);
            CommandBarView commandBar = Assert.IsType<CommandBarView>(window.FindControl<CommandBarView>("CommandBarRegion"));
            Button account = Assert.IsType<Button>(commandBar.FindControl<Button>("AccountHeaderButton"));
            TextBlock badge = Assert.Single(account.GetVisualDescendants().OfType<TextBlock>());
            Assert.Equal(vm.AccountHeaderBadgeText, badge.Text);
            Assert.Equal(vm.AccountHeaderBadgeText, ToolTip.GetTip(account));
            Assert.Equal(TextTrimming.CharacterEllipsis, badge.TextTrimming);
            Assert.True(badge.MaxWidth < 100);
            Assert.Equal("Free", vm.AccountPlanBadgeText);
            Assert.Contains("3/5", vm.CloudQuotaBadgeText, StringComparison.Ordinal);

            vm.LicenseKeyInput = "ZIWA-PRO-1";
            await vm.ActivateLicenseKeyCommand.ExecuteAsync(null);

            Assert.Equal("Pro", vm.AccountPlanBadgeText);
            Assert.Contains("13/15", vm.CloudQuotaBadgeText, StringComparison.Ordinal);

            await vm.LogoutAccountCommand.ExecuteAsync(null);
            Assert.False(vm.IsUserAuthenticated);
            Assert.Equal("الحساب", vm.AccountHeaderBadgeText);

            window.Close();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static HttpResponseMessage JsonResp(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
