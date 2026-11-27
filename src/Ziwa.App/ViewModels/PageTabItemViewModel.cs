using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.ViewModels;

public sealed partial class PageTabItemViewModel(MainWindowViewModel workspace, PageId id) : ObservableObject
{
    public PageId Id { get; } = id;
    public string DisplayLabel => $"{Index + 1}. {Name}";
    public string AccessibleLabel => IsActive ? $"{DisplayLabel} — الصفحة النشطة" : DisplayLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel), nameof(AccessibleLabel))]
    [NotifyCanExecuteChangedFor(nameof(MoveEarlierCommand), nameof(MoveLaterCommand))]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel), nameof(AccessibleLabel))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleLabel))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveLaterCommand), nameof(DeleteCommand))]
    private int _pageCount;

    private bool CanMoveEarlier => Index > 0;
    private bool CanMoveLater => Index < PageCount - 1;
    private bool CanDelete => PageCount > 1;

    [RelayCommand]
    private void Duplicate() => workspace.DuplicatePage(Id);

    [RelayCommand(CanExecute = nameof(CanMoveEarlier))]
    private void MoveEarlier() => workspace.MovePage(Id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveLater))]
    private void MoveLater() => workspace.MovePage(Id, 1);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => workspace.DeletePage(Id);
}
