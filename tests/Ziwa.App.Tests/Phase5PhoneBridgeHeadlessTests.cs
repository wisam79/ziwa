using System.Net;
using System.Net.Http.Headers;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Infrastructure.Bridge;

namespace Ziwa.App.Tests;

public sealed class Phase5PhoneBridgeHeadlessTests
{
    [AvaloniaFact]
    public async Task PhoneBridgeDialog_GeneratesQr_StaysAliveWhenHidden_AndInsertsUploadedPhoto()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p502-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                storageOptions: new ProjectStorageOptions(tempRoot),
                phoneBridgeOptions: new PhoneBridgeOptions
                {
                    PreferredPort = 0,
                    BindLoopbackOnly = true,
                    EnableCloudSync = false,
                });

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
            window.Show();

            PhoneBridgeView dialog = Assert.Single(window.GetVisualDescendants().OfType<PhoneBridgeView>());
            Assert.Equal(FlowDirection.RightToLeft, dialog.FlowDirection);
            foreach (Button btn in dialog.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)), $"Missing AutomationProperties.Name on '{btn.Name}'.");
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            await vm.OpenPhoneBridgeDialogCommand.ExecuteAsync(null);
            Assert.True(vm.IsPhoneBridgeDialogOpen);
            Assert.True(vm.IsPhoneBridgeRunning);
            Assert.NotNull(vm.PhoneBridgeQrBitmap);
            Assert.True(vm.PhoneBridgeQrBitmap!.Width >= 200);
            Assert.StartsWith("http://127.0.0.1:", vm.PhoneBridgeLocalUrl, StringComparison.Ordinal);

            // Closing the QR dialog must NOT stop the bridge (photographer keeps shooting)
            vm.ClosePhoneBridgeDialogCommand.Execute(null);
            Assert.False(vm.IsPhoneBridgeDialogOpen);
            Assert.True(vm.IsPhoneBridgeRunning);

            int initialNodeCount = vm.ActivePage.Nodes.Length;
            var localUri = new Uri(vm.PhoneBridgeLocalUrl);
            string token = localUri.Query.Replace("?token=", string.Empty, StringComparison.Ordinal);

            using var http = new HttpClient();
            using var form = new MultipartFormDataContent();
            using var pngBmp = new SKBitmap(20, 14, SKColorType.Rgba8888, SKAlphaType.Premul);
            pngBmp.Erase(SKColors.CornflowerBlue);
            using SKData pngData = pngBmp.Encode(SKEncodedImageFormat.Png, 100);
            var filePart = new ByteArrayContent(pngData.ToArray());
            filePart.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(filePart, "photo", "studio_capture.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{localUri.GetLeftPart(UriPartial.Authority)}/upload")
            {
                Content = form,
            };
            req.Headers.Add("X-Bridge-Token", token);

            using HttpResponseMessage resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Pump UI dispatcher until PhotoReceived inserts the ImageNode
            for (int i = 0; i < 40 && vm.ActivePage.Nodes.Length == initialNodeCount; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            Assert.Equal(initialNodeCount + 1, vm.ActivePage.Nodes.Length);
            Assert.Equal(1, vm.PhoneBridgeReceivedCount);
            ImageNode inserted = Assert.IsType<ImageNode>(vm.ActivePage.Nodes[^1]);
            Assert.Equal("studio_capture", inserted.Name);

            await vm.StopPhoneBridgeCommand.ExecuteAsync(null);
            Assert.False(vm.IsPhoneBridgeRunning);
            Assert.Null(vm.PhoneBridgeQrBitmap);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
