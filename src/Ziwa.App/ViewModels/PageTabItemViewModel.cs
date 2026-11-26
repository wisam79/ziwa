using CommunityToolkit.Mvvm.ComponentModel;

namespace Ziwa.App.ViewModels;

public sealed partial class PageTabItemViewModel(int index, string name, bool isActive) : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string DisplayLabel => $"{Index + 1}. {Name}";
    public string AccessibleLabel => IsActive ? $"{DisplayLabel} — الصفحة النشطة" : DisplayLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    private bool _isActive = isActive;
}
