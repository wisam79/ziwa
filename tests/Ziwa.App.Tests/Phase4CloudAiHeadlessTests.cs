using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Inspector;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Ai;

namespace Ziwa.App.Tests;

public sealed class Phase4CloudAiHeadlessTests
{
    [AvaloniaFact]
    public async Task CloudEnhanceUi_ShowsQuotaBadgeEnhancesImageAndResetsOnLogout()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p404-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            byte[] upscaledJpeg = CreateSolidJpegBytes(320, 440, new SKColor(200, 170, 150));
            string modalPayload = JsonSerializer.Serialize(new
            {
                success = true,
                usage_recorded = true,
                execution_seconds = 1.9,
                used_today = 2,
                daily_limit = 15,
                image = "data:image/jpeg;base64," + Convert.ToBase64String(upscaledJpeg),
            });

            using var handler = new StubHttpHandler(modalPayload);
            var cloudOptions = new CloudAiOptions
            {
                CustomHttpHandler = handler,
            };

            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot),
                cloudAiOptions: cloudOptions);

            ICloudAuthSessionStore authStore = sp.GetRequiredService<ICloudAuthSessionStore>();
            authStore.SetSession(new CloudAuthSessionSnapshot("jwt-ui-test", "user-ui", "pro@ziwa.app", "pro"));

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            ImageNode target = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n is ImageNode));
            AssetId originalId = target.OriginalAssetId;
            vm.Session.SetSelection([target.Id]);

            InspectorUi.Layout(window);
            var properties = InspectorUi.Visible<InspectorPropertiesView>(window);
            var cloudCard = InspectorUi.Visible<StudioCard>(properties, "CloudAiCard");
            var inspectorBtn = InspectorUi.Visible<Button>(cloudCard, "InspectorCloudEnhanceButton");
            Assert.Same(vm.EnhancePhotoCloudCommand, inspectorBtn.Command);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(inspectorBtn)));
            Assert.NotNull(ToolTip.GetTip(inspectorBtn));
            Button more = Assert.IsType<Button>(window.FindControl<Button>("QuickMoreButton"));
            MenuFlyout menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more);
            try
            {
                window.UpdateLayout();
                MenuItem enhance = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Command == vm.EnhancePhotoCloudCommand);
                Assert.Contains("سحابي", Assert.IsType<string>(enhance.Header));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(enhance)));
                Assert.NotNull(ToolTip.GetTip(enhance));
            }
            finally
            {
                menu.Hide();
            }

            Assert.Contains("15", vm.CloudQuotaBadgeText, StringComparison.Ordinal);

            await vm.EnhancePhotoCloudCommand.ExecuteAsync(null);

            ImageNode enhanced = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == target.Id));
            Assert.NotEqual(originalId, enhanced.CurrentAssetId);
            Assert.Equal(originalId, enhanced.OriginalAssetId);
            Assert.Contains("13/15", vm.CloudQuotaBadgeText, StringComparison.Ordinal);

            // Logout clears quota badge immediately
            authStore.ClearSession();
            Assert.Equal("تسجيل الدخول مطلوب للترميم السحابي", vm.CloudQuotaBadgeText);

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

    private static byte[] CreateSolidJpegBytes(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(color);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly string _jsonResponse;

        public StubHttpHandler(string jsonResponse)
        {
            _jsonResponse = jsonResponse;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonResponse, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
