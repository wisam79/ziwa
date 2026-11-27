using Avalonia.Controls.Notifications;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Collage;
using Ziwa.Application.Interaction;
using Ziwa.Application.Pages;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.ViewModels;

public sealed record PaperPresetOption(string TitleAr, double WidthMm, double HeightMm);

public sealed record CollagePresetOption(string PresetId, string TitleAr, CollageGridSpec Spec);

public sealed record CollageSizingModeOption(CollageSizingMode Mode, string TitleAr);

public sealed record GridAlignmentOption(GridAlignment9 Alignment, string TitleAr);

public sealed partial class MainWindowViewModel
{
    private bool _isSyncingInspectorFields;
    private CollageGridSpec? _syncedCollageSpec;
    private SlotId? _syncedCollageSlotId;
    private double _syncedSlotCornerRadiusMm;
    private StrokeSpec? _syncedSlotBorder;

    public event Action<string, string, NotificationType>? ToastRequested;

    public IReadOnlyList<PaperPresetOption> AvailablePaperPresets { get; } =
    [
        new("A4 (210×297 مم)", 210.0, 297.0),
        new("A5 (148×210 مم)", 148.0, 210.0),
        new("4×6 بوصة (101.6×152.4 مم)", 101.6, 152.4),
        new("5×7 بوصة (127×177.8 مم)", 127.0, 177.8),
        new("بطاقة CR80 (85.6×54 مم)", 85.6, 54.0),
    ];

    public IReadOnlyList<CollagePresetOption> AvailableCollagePresetOptions { get; } =
    [
        new("id-35x45", "جواز سفر 35×45 مم (3×2)", CollagePresets.IdPassport35x45),
        new("id-40x60", "هوية رسمية 40×60 مم (2×2)", CollagePresets.IdOfficial40x60),
        new("visa-50x50", "تأشيرة مربعة 50×50 مم (2×1)", CollagePresets.VisaSquare50x50),
        new("mixed-identity", "شيت هوية مختلط (40×60 + 30×40)", CollagePresets.MixedIdentitySheet),
        new("custom-2x2-fit", "شبكة 2×2 ملء الورقة", CollagePresets.Custom2x2Fit),
    ];

    public IReadOnlyList<CollageSizingModeOption> AvailableCollageSizingOptions { get; } =
    [
        new(CollageSizingMode.FixedPhysicalCellMm, "مقاس خلية ثابت (مم)"),
        new(CollageSizingMode.FitGridToPaper, "تمديد لملء الورقة"),
    ];

    public IReadOnlyList<GridAlignmentOption> AvailableCollageAlignmentOptions { get; } =
    [
        new(GridAlignment9.MiddleCenter, "توسيط كامل"),
        new(GridAlignment9.TopCenter, "أعلى الوسط"),
        new(GridAlignment9.TopRight, "أعلى اليمين"),
        new(GridAlignment9.TopLeft, "أعلى اليسار"),
        new(GridAlignment9.MiddleRight, "وسط اليمين"),
        new(GridAlignment9.MiddleLeft, "وسط اليسار"),
        new(GridAlignment9.BottomCenter, "أسفل الوسط"),
        new(GridAlignment9.BottomRight, "أسفل اليمين"),
        new(GridAlignment9.BottomLeft, "أسفل اليسار"),
    ];

    public IReadOnlyList<string> AvailableFontFamilies { get; } =
    [
        "Cairo",
        "Tajawal",
        "IBM Plex Sans Arabic",
    ];

    [ObservableProperty]
    private Color _selectedTextColor = Colors.Black;

    [ObservableProperty]
    private double _selectedTextFontSizePt = 24.0;

    [ObservableProperty]
    private string _selectedTextFontFamily = "Cairo";

    [ObservableProperty]
    private Color _selectedShapeFillColor = Color.FromRgb(11, 92, 171);

    [ObservableProperty]
    private double _selectedShapeOpacityPercent = 100.0;

    [ObservableProperty]
    private Color _selectedSvgTintColor = Color.FromRgb(11, 92, 171);

    [ObservableProperty]
    private PaperPresetOption? _selectedPaperPreset;

    [ObservableProperty]
    private double _pageWidthInputMm = 210.0;

    [ObservableProperty]
    private double _pageHeightInputMm = 297.0;

    [ObservableProperty]
    private double _pageBleedInputMm;

    [ObservableProperty]
    private CollagePresetOption? _selectedCollagePresetOption;

    [ObservableProperty]
    private CollageSizingModeOption? _selectedCollageSizingOption;

    [ObservableProperty]
    private GridAlignmentOption? _selectedCollageAlignmentOption;

    [ObservableProperty]
    private double _collageCellWidthInputMm = 40.0;

    [ObservableProperty]
    private double _collageCellHeightInputMm = 60.0;

    [ObservableProperty]
    private int _collageRowsInput = 2;

    [ObservableProperty]
    private int _collageColumnsInput = 2;

    [ObservableProperty]
    private double _collageGapInputMm = 4.0;

    [ObservableProperty]
    private double _collageMarginInputMm = 6.0;

    [ObservableProperty]
    private double _collageSlotCornerRadiusMm;

    [ObservableProperty]
    private double _collageSlotBorderWidthMm;

    [ObservableProperty]
    private Color _collageSlotBorderColor = Color.FromRgb(15, 23, 42);

    public bool IsShapeSelected => SelectedNodes.Count == 1 && SelectedNodes[0] is ShapeNode;
    public bool IsCollageFixedPhysicalSizing => SelectedCollageSizingOption?.Mode != CollageSizingMode.FitGridToPaper;
    public bool IsCollageAutoRepeatEnabled => ActivePage.CollageLayout?.AutoRepeatMode == true;
    public string AutoRepeatToggleText => IsCollageAutoRepeatEnabled
        ? "التكرار التلقائي: مُفعّل ✓"
        : "تفعيل التكرار التلقائي";
    public bool CanFillSheetWithSelectedSlot => IsCollageSlotWithImageSelected && IsCollageMode;
    public string CollageSheetSummaryText
    {
        get
        {
            if (ActivePage.CollageLayout is not { } layout)
            {
                return string.Empty;
            }

            int filled = layout.Slots.Count(s => s.AssignedAssetId is { IsEmpty: false });
            int total = layout.Slots.Length;
            string sizeLabel = !layout.Spec.MixedBands.IsDefaultOrEmpty
                ? "مقاسات مختلطة"
                : layout.Spec.SizingMode == CollageSizingMode.FitGridToPaper
                    ? $"{layout.Spec.Rows}×{layout.Spec.Columns} ملء الورقة"
                    : $"{layout.Spec.Rows}×{layout.Spec.Columns} · {layout.Spec.CellWidthMm:0.#}×{layout.Spec.CellHeightMm:0.#} مم";
            return $"{sizeLabel} · {filled}/{total} ممتلئة";
        }
    }

    public int CollagePendingCount => ActivePage.CollageLayout is { PendingAssets.IsDefault: false } layout
        ? layout.PendingAssets.Length
        : 0;
    public bool HasPendingCollageAssets => CollagePendingCount > 0;
    public string CollagePendingBadgeText => $"صور في قائمة الانتظار: {CollagePendingCount}";
    public string FilterBrightnessLabel => $"{FilterBrightness * 100.0:+0;-0;0}%";
    public string FilterContrastLabel => $"{FilterContrast * 100.0:+0;-0;0}%";
    public string FilterSaturationLabel => $"{FilterSaturation * 100.0:+0;-0;0}%";

    public void ShowToast(string title, string message, NotificationType type = NotificationType.Information)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        DispatchToUi(() => ToastRequested?.Invoke(title, message, type));
    }

    partial void OnFilterBrightnessChanged(double value) => OnPropertyChanged(nameof(FilterBrightnessLabel));
    partial void OnFilterContrastChanged(double value) => OnPropertyChanged(nameof(FilterContrastLabel));
    partial void OnFilterSaturationChanged(double value) => OnPropertyChanged(nameof(FilterSaturationLabel));

    partial void OnSelectedCollagePresetOptionChanged(CollagePresetOption? value)
    {
        if (_isSyncingInspectorFields || value is null || !IsCollageMode)
        {
            return;
        }

        _session.ApplyCommand($"تطبيق قالب الكولاج: {value.TitleAr}", doc => CollageOperations.EnableOrUpdateCollageSpec(doc, value.Spec));
        StatusMessage = $"تم تطبيق قالب {value.TitleAr}";
    }

    partial void OnSelectedCollageSizingOptionChanged(CollageSizingModeOption? value) =>
        OnPropertyChanged(nameof(IsCollageFixedPhysicalSizing));

    partial void OnCollageSlotBorderColorChanged(Color value)
    {
        if (_isSyncingInspectorFields ||
            _session.SelectedCollageSlotId is not { } sid ||
            CollageSlotBorderWidthMm <= 0.0)
        {
            return;
        }

        var border = new StrokeSpec(ToColorRgba(value), Math.Clamp(CollageSlotBorderWidthMm, 0.1, 10.0));
        _session.ApplyCommand(
            "تغيير لون إطار الخانة",
            doc => CollageOperations.SetSlotBorder(doc, sid, border),
            coalesceKey: "slot.border.color");
    }

    partial void OnSelectedPaperPresetChanged(PaperPresetOption? value)
    {
        if (_isSyncingInspectorFields || value is null)
        {
            return;
        }

        PageWidthInputMm = value.WidthMm;
        PageHeightInputMm = value.HeightMm;
        ApplyPageGeometry();
    }

    partial void OnSelectedTextColorChanged(Color value)
    {
        if (!_isSyncingInspectorFields && SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode txt)
        {
            var rgba = ToColorRgba(value);
            _session.ApplyCommand("تغيير لون النص", doc =>
                FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, color: rgba), coalesceKey: "text.color");
        }
    }

    partial void OnSelectedShapeFillColorChanged(Color value)
    {
        if (!_isSyncingInspectorFields && SelectedNodes.Count == 1 && SelectedNodes[0] is ShapeNode shape)
        {
            var rgba = ToColorRgba(value);
            _session.ApplyCommand("تغيير لون الشكل", doc =>
                FreeformSceneOperations.UpdateShapeNodeStyle(doc, shape.Id, fillColor: rgba), coalesceKey: "shape.fill");
        }
    }

    partial void OnSelectedSvgTintColorChanged(Color value)
    {
        if (!_isSyncingInspectorFields && SelectedNodes.Count == 1 && SelectedNodes[0] is SvgNode svg)
        {
            var rgba = ToColorRgba(value);
            _session.ApplyCommand("تلوين الشعار", doc =>
                FreeformSceneOperations.SetSvgTint(doc, svg.Id, rgba), coalesceKey: "svg.tint");
        }
    }

    [RelayCommand]
    private void ApplySelectedTextStyle()
    {
        if (SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt)
        {
            return;
        }

        ColorRgba rgba = ToColorRgba(SelectedTextColor);
        _session.ApplyCommand("تعديل تنسيق النص", doc =>
            FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, rgba, SelectedTextFontSizePt, SelectedTextFontFamily));
        StatusMessage = "تم تحديث تنسيق النص";
    }

    [RelayCommand]
    private void ApplySelectedShapeStyle()
    {
        if (SelectedNodes.Count != 1 || SelectedNodes[0] is not ShapeNode shape)
        {
            return;
        }

        ColorRgba fillRgba = ToColorRgba(SelectedShapeFillColor);
        ColorRgba strokeRgba = ToColorRgba(SelectedShapeStrokeColor);
        double opacity = Math.Clamp(SelectedShapeOpacityPercent / 100.0, 0.05, 1.0);
        double strokeW = Math.Clamp(SelectedShapeStrokeWidthMm, 0.0, 20.0);
        _session.ApplyCommand("تعديل نمط الشكل", doc =>
            FreeformSceneOperations.UpdateShapeNodeStyle(
                doc,
                shape.Id,
                fillColor: fillRgba,
                strokeColor: strokeRgba,
                opacity: opacity,
                strokeWidthMm: strokeW));
        StatusMessage = "تم تحديث نمط الشكل";
    }

    [RelayCommand]
    private void ClearSvgTint()
    {
        if (SelectedNodes.Count != 1 || SelectedNodes[0] is not SvgNode svg)
        {
            return;
        }

        _session.ApplyCommand("إزالة تلوين الشعار", doc => FreeformSceneOperations.SetSvgTint(doc, svg.Id, null));
        StatusMessage = "تمت استعادة ألوان الشعار الأصلية";
    }

    [RelayCommand]
    private void ApplyPageGeometry()
    {
        double w = Math.Clamp(PageWidthInputMm, 10.0, 2000.0);
        double h = Math.Clamp(PageHeightInputMm, 10.0, 2000.0);
        double bleed = Math.Clamp(PageBleedInputMm, 0.0, 25.0);

        _session.ApplyCommand("تغيير مقاس الورقة والنزيف", doc =>
        {
            ZiwaDocument updated = MultiPageOperations.SyncPaperAndBleedAcrossAllPages(
                doc,
                w,
                h,
                bleed,
                doc.ActivePage.SafeMarginMm);

            if (updated.ActivePage.CollageLayout is { } layout)
            {
                updated = CollageOperations.EnableOrUpdateCollageSpec(updated, layout.Spec);
            }

            return updated;
        });

        StatusMessage = $"تم ضبط مقاس الورقة: {w:0.#}×{h:0.#} مم";
        ShowToast("مقاس الورقة", StatusMessage, NotificationType.Success);
    }

    [RelayCommand]
    private void ApplyCollageGridAdjustments()
    {
        if (ActivePage.CollageLayout is not { } layout)
        {
            return;
        }

        int rows = Math.Clamp(CollageRowsInput, 1, 12);
        int cols = Math.Clamp(CollageColumnsInput, 1, 12);
        double cellW = Math.Clamp(CollageCellWidthInputMm, 10.0, 250.0);
        double cellH = Math.Clamp(CollageCellHeightInputMm, 10.0, 250.0);
        double gap = Math.Clamp(CollageGapInputMm, 0.0, 25.0);
        double margin = Math.Clamp(CollageMarginInputMm, 0.0, 30.0);
        CollageSizingMode sizing = SelectedCollageSizingOption?.Mode ?? CollageSizingMode.FixedPhysicalCellMm;
        GridAlignment9 alignment = SelectedCollageAlignmentOption?.Alignment ?? GridAlignment9.MiddleCenter;

        CollageGridSpec nextSpec = layout.Spec with
        {
            PresetId = "custom",
            SizingMode = sizing,
            Rows = rows,
            Columns = cols,
            CellWidthMm = cellW,
            CellHeightMm = cellH,
            GapXMm = gap,
            GapYMm = gap,
            MarginMm = margin,
            Alignment = alignment,
            MixedBands = default,
        };

        _session.ApplyCommand("تعديل شبكة الكولاج", doc => CollageOperations.EnableOrUpdateCollageSpec(doc, nextSpec));
        StatusMessage = $"تم تحديث شبكة الكولاج ({rows}×{cols})";
    }

    [RelayCommand]
    private void ApplyMaskToAllSlots()
    {
        if (!IsCollageMode || ActivePage.CollageLayout is not { Slots.IsDefaultOrEmpty: false } layout)
        {
            return;
        }

        SlotId targetId = _session.SelectedCollageSlotId ?? layout.Slots[0].Id;
        double corner = Math.Clamp(CollageSlotCornerRadiusMm, 0.0, 25.0);
        _session.ApplyCommand(
            "تعميم القناع على كل الخانات",
            doc => CollageOperations.SetSlotMask(doc, targetId, SelectedImageMask, corner, applyToAllSlots: true));
        StatusMessage = "تم تطبيق القناع على جميع خانات الشيت";
    }

    [RelayCommand]
    private void ApplySlotStyle() => ApplySlotStyleCore(applyToAllSlots: false);

    [RelayCommand]
    private void ApplySlotStyleToAll() => ApplySlotStyleCore(applyToAllSlots: true);

    private void ApplySlotStyleCore(bool applyToAllSlots)
    {
        if (!IsCollageMode || ActivePage.CollageLayout is not { Slots.IsDefaultOrEmpty: false } layout)
        {
            return;
        }

        SlotId? targetId = _session.SelectedCollageSlotId ?? (applyToAllSlots ? layout.Slots[0].Id : null);
        if (targetId is not { } sid)
        {
            return;
        }

        double corner = Math.Clamp(CollageSlotCornerRadiusMm, 0.0, 25.0);
        double borderWidth = Math.Clamp(CollageSlotBorderWidthMm, 0.0, 10.0);
        StrokeSpec? border = borderWidth > 0.05
            ? new StrokeSpec(ToColorRgba(CollageSlotBorderColor), borderWidth)
            : null;

        string label = applyToAllSlots ? "تعميم الإطار والزوايا على كل الخانات" : "تعديل إطار وزوايا الخانة";
        _session.ApplyCommand(label, doc =>
        {
            ZiwaDocument withMask = CollageOperations.SetSlotMask(doc, sid, SelectedImageMask, corner, applyToAllSlots);
            return CollageOperations.SetSlotBorder(withMask, sid, border, applyToAllSlots);
        });
        StatusMessage = applyToAllSlots ? "تم تعميم الإطار والزوايا على جميع الخانات" : "تم تحديث إطار وزوايا الخانة";
    }

    [RelayCommand]
    private void FillAllSlotsWithSelectedSlot()
    {
        if (!IsCollageMode || SelectedCollageSlot?.AssignedAssetId is not { IsEmpty: false } asset)
        {
            return;
        }

        _session.ApplyCommand(
            "تكرار صورة الخانة على كل الشيت",
            doc => CollageOperations.SetAutoRepeatMode(doc, enabled: true, seedAsset: asset));
        StatusMessage = "تم تعميم الصورة على جميع خانات الشيت";
    }

    [RelayCommand]
    private void ClearSelectedSlot()
    {
        if (_session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("تفريغ خانة الكولاج", doc => CollageOperations.ClearSlot(doc, sid));
            StatusMessage = "تم تفريغ خانة الكولاج";
        }
    }

    [RelayCommand]
    private void ClearAllCollageSlots()
    {
        if (!IsCollageMode)
        {
            return;
        }

        _session.ApplyCommand("تفريغ جميع خانات الكولاج", CollageOperations.ClearAllSlots);
        StatusMessage = "تم تفريغ جميع خانات شيت الكولاج";
    }

    [RelayCommand]
    private void ResetSelectedSlotTransform()
    {
        if (_session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("إعادة ضبط تأطير الخانة", doc => CollageOperations.ResetSlotTransformAndCrop(doc, sid));
            StatusMessage = "تمت إعادة ضبط تأطير وتدوير الخانة";
        }
    }

    [RelayCommand]
    private void AutoDistributePendingCollageAssets()
    {
        if (!HasPendingCollageAssets)
        {
            return;
        }

        _session.ApplyCommand("توزيع الصور المنتظرة على الخانات", CollageOperations.AutoDistributePendingToSlots);
        StatusMessage = "تم توزيع الصور المنتظرة على خانات الكولاج";
    }

    private void SyncInspectorPolishFields()
    {
        _isSyncingInspectorFields = true;
        try
        {
            PageWidthInputMm = ActivePage.WidthMm;
            PageHeightInputMm = ActivePage.HeightMm;
            PageBleedInputMm = ActivePage.BleedMm;
            SelectedPaperPreset = AvailablePaperPresets.FirstOrDefault(p =>
                Math.Abs(p.WidthMm - ActivePage.WidthMm) < 0.5 &&
                Math.Abs(p.HeightMm - ActivePage.HeightMm) < 0.5);

            if (ActivePage.CollageLayout is { } layout)
            {
                if (!Equals(_syncedCollageSpec, layout.Spec))
                {
                    _syncedCollageSpec = layout.Spec;
                    SelectedCollagePresetOption = AvailableCollagePresetOptions.FirstOrDefault(p =>
                        string.Equals(p.PresetId, layout.Spec.PresetId, StringComparison.OrdinalIgnoreCase));
                    SelectedCollageSizingOption = AvailableCollageSizingOptions.FirstOrDefault(s => s.Mode == layout.Spec.SizingMode)
                        ?? AvailableCollageSizingOptions[0];
                    SelectedCollageAlignmentOption = AvailableCollageAlignmentOptions.FirstOrDefault(a => a.Alignment == layout.Spec.Alignment)
                        ?? AvailableCollageAlignmentOptions[0];
                    CollageCellWidthInputMm = Math.Round(layout.Spec.CellWidthMm, 1);
                    CollageCellHeightInputMm = Math.Round(layout.Spec.CellHeightMm, 1);
                    CollageRowsInput = layout.Spec.Rows;
                    CollageColumnsInput = layout.Spec.Columns;
                    CollageGapInputMm = layout.Spec.GapXMm;
                    CollageMarginInputMm = layout.Spec.MarginMm;
                }

                if (SelectedCollageSlot is { } slot)
                {
                    bool slotChanged = _syncedCollageSlotId != slot.Id;
                    if (slotChanged || Math.Abs(_syncedSlotCornerRadiusMm - slot.CornerRadiusMm) > 0.0001)
                    {
                        _syncedSlotCornerRadiusMm = slot.CornerRadiusMm;
                        CollageSlotCornerRadiusMm = Math.Round(slot.CornerRadiusMm, 1);
                    }

                    if (slotChanged || !Equals(_syncedSlotBorder, slot.Border))
                    {
                        _syncedSlotBorder = slot.Border;
                        if (slot.Border is { } sb)
                        {
                            CollageSlotBorderWidthMm = Math.Round(sb.WidthMm, 1);
                            CollageSlotBorderColor = ToAvaloniaColor(sb.Color);
                        }
                        else
                        {
                            CollageSlotBorderWidthMm = 0.0;
                        }
                    }

                    _syncedCollageSlotId = slot.Id;
                }
                else
                {
                    _syncedCollageSlotId = null;
                }
            }
            else
            {
                _syncedCollageSpec = null;
                _syncedCollageSlotId = null;
            }

            if (SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode txt)
            {
                SelectedTextColor = ToAvaloniaColor(txt.Color);
                SelectedTextFontSizePt = txt.FontSizePt;
                SelectedTextFontFamily = AvailableFontFamilies.FirstOrDefault(f =>
                    string.Equals(f, txt.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? "Cairo";
            }
            else if (SelectedNodes.Count == 1 && SelectedNodes[0] is ShapeNode shape)
            {
                if (shape.Fill is SolidShapeFill solid)
                {
                    SelectedShapeFillColor = ToAvaloniaColor(solid.Color);
                }

                SelectedShapeOpacityPercent = Math.Round(shape.Opacity * 100.0);
            }
            else if (SelectedNodes.Count == 1 && SelectedNodes[0] is SvgNode svg && svg.TintColor is { } tint)
            {
                SelectedSvgTintColor = ToAvaloniaColor(tint);
            }
        }
        finally
        {
            _isSyncingInspectorFields = false;
        }

        OnPropertyChanged(nameof(IsShapeSelected));
        OnPropertyChanged(nameof(IsCollageFixedPhysicalSizing));
        OnPropertyChanged(nameof(IsCollageAutoRepeatEnabled));
        OnPropertyChanged(nameof(AutoRepeatToggleText));
        OnPropertyChanged(nameof(CanFillSheetWithSelectedSlot));
        OnPropertyChanged(nameof(CollageSheetSummaryText));
        OnPropertyChanged(nameof(CollagePendingCount));
        OnPropertyChanged(nameof(HasPendingCollageAssets));
        OnPropertyChanged(nameof(CollagePendingBadgeText));
        SyncExtendedInspectorFields();
    }

    private static Color ToAvaloniaColor(ColorRgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    private static ColorRgba ToColorRgba(Color c) => new(c.R, c.G, c.B, c.A);
}
