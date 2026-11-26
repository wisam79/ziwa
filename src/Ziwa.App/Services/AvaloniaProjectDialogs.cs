using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Ziwa.Application.Persistence;

namespace Ziwa.App.Services;

public sealed class AvaloniaProjectDialogs : IProjectDialogs
{
    private static readonly FilePickerFileType ProjectType = new("مشروع Ziwa") { Patterns = ["*.ziwa"] };
    private static readonly FilePickerFileType BackupType = new("نسخة مكتبة Ziwa") { Patterns = ["*.ziwabackup"] };
    private static readonly FilePickerFileType ImageType = new("صور نقطية") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] };
    public Window? Owner { get; set; }

    public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) =>
        PickOpenAsync("فتح مشروع", ProjectType, cancellationToken);

    public Task<string?> PickOpenImageAsync(CancellationToken cancellationToken = default) =>
        PickOpenAsync("إدراج صورة", ImageType, cancellationToken);

    public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) =>
        PickSaveAsync("حفظ مشروع باسم", "project.ziwa", "ziwa", ProjectType, cancellationToken);

    public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) =>
        PickOpenAsync("استيراد نسخة احتياطية", BackupType, cancellationToken);

    public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) =>
        PickSaveAsync("تصدير نسخة احتياطية", "library.ziwabackup", "ziwabackup", BackupType, cancellationToken);

    public async Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Owner is null) { return UnsavedChangesChoice.Cancel; }
        var dialog = new Window
        {
            Title = "تعديلات غير محفوظة",
            Width = 460,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = Avalonia.Media.FlowDirection.RightToLeft,
            Background = Owner.Background,
            Foreground = Owner.Foreground,
            FontFamily = Owner.FontFamily,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };
        foreach ((string label, UnsavedChangesChoice choice) in new[]
        {
            ("حفظ", UnsavedChangesChoice.Save), ("تجاهل التعديلات", UnsavedChangesChoice.Discard), ("إلغاء", UnsavedChangesChoice.Cancel),
        })
        {
            var button = new Button { Content = label, MinHeight = 36 };
            button.Classes.Add(choice == UnsavedChangesChoice.Save ? "primary" : "secondary");
            if (Owner.TryFindResource("ControlCornerRadius", out object? radius) && radius is CornerRadius corners) { button.CornerRadius = corners; }
            if (Owner.TryFindResource("ZiwaSubtleFillBrush", out object? fill)) { button.Background = fill as Avalonia.Media.IBrush; }
            if (Owner.TryFindResource("ZiwaPrimaryTextBrush", out object? text)) { button.Foreground = text as Avalonia.Media.IBrush; }
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            button.Click += (_, _) => dialog.Close(choice);
            buttons.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 24,
            Children = { new TextBlock { Text = "توجد تعديلات غير محفوظة. هل تريد حفظها قبل المتابعة؟", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(UnsavedChangesChoice.Cancel)));
        UnsavedChangesChoice result = await dialog.ShowDialog<UnsavedChangesChoice>(Owner);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<string?> PickOpenAsync(string title, FilePickerFileType type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Owner?.StorageProvider is not { CanOpen: true } provider) { return null; }
        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [type],
        });
        ct.ThrowIfCancellationRequested();
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveAsync(string title, string name, string extension, FilePickerFileType type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Owner?.StorageProvider is not { CanSave: true } provider) { return null; }
        IStorageFile? file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            DefaultExtension = extension,
            FileTypeChoices = [type],
            ShowOverwritePrompt = true,
        });
        ct.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }
}
