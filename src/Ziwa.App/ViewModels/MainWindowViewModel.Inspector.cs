using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Interaction;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.ViewModels;

public sealed record MaskOption(MaskShapeKind Kind, string TitleAr);

public sealed record FilterPresetOption(PhotoFilterPreset Preset, string TitleAr);

public sealed record TextCurveOption(TextCurveKind Kind, string TitleAr);

public sealed record SceneLayerItemViewModel(
    NodeId Id,
    string Name,
    string KindBadgeAr,
    string DimensionsText,
    bool IsVisible,
    bool IsLocked,
    bool IsSelected)
{
    public string VisibilityLabel => IsVisible ? "إخفاء" : "إظهار";
    public string LockLabel => IsLocked ? "فتح" : "قفل";
    public bool CanSelectOnCanvas => IsVisible && !IsLocked;
    public string SelectAccessibleName => $"تحديد الطبقة {Name}";
    public string VisibilityAccessibleName => $"{VisibilityLabel} الطبقة {Name}";
    public string LockAccessibleName => $"{LockLabel} الطبقة {Name}";
    public string LayerTip => IsLocked
        ? "طبقة مقفلة — افتح القفل لتحديدها وتحريرها"
        : (IsVisible ? "انقر لتحديد هذا العنصر على اللوحة" : "طبقة مخفية — أظهرها أولاً");
}

public sealed partial class MainWindowViewModel
{
    private const int PropertiesTabIndex = 0;
    private const int PageTabIndex = 2;

    [ObservableProperty]
    private int _inspectorTabIndex = PropertiesTabIndex;

    public event Action? InspectorPreflightReviewRequested;

    partial void OnInspectorTabIndexChanged(int value)
    {
        if (value != PropertiesTabIndex)
        {
            _autoOpenedInspector = false;
        }
    }

    [RelayCommand]
    private void ShowInspectorPage() => InspectorTabIndex = PageTabIndex;

    [RelayCommand]
    private void ShowInspectorWarnings()
    {
        ShowInspectorPage();
        InspectorPreflightReviewRequested?.Invoke();
    }

    public ObservableCollection<SceneLayerItemViewModel> LayerItems { get; } = [];
    private NodeId? _syncedTransformNodeId;
    private RectMm _syncedTransformBounds;
    private double _syncedTransformRotation;

    public IReadOnlyList<MaskOption> AvailableMaskOptions { get; } =
    [
        new(MaskShapeKind.None, "بلا قناع"),
        new(MaskShapeKind.Circle, "دائرة"),
        new(MaskShapeKind.Arch, "قوس"),
        new(MaskShapeKind.Hexagon, "سداسي"),
        new(MaskShapeKind.Diamond, "معين"),
        new(MaskShapeKind.Heart, "قلب"),
        new(MaskShapeKind.Star, "نجمة"),
        new(MaskShapeKind.Seal, "ختم"),
    ];

    public IReadOnlyList<FilterPresetOption> AvailableFilterPresetOptions { get; } =
    [
        new(PhotoFilterPreset.None, "أصلي (بلا مرشح)"),
        new(PhotoFilterPreset.WarmStudio, "استوديو دافئ"),
        new(PhotoFilterPreset.CoolCrisp, "بارد نقي"),
        new(PhotoFilterPreset.ClassicBw, "أبيض وأسود كلاسيكي"),
        new(PhotoFilterPreset.HighKeyId, "إضاءة هوية ناصعة"),
        new(PhotoFilterPreset.SepiaVintage, "سيبيا عتيق"),
    ];

    [ObservableProperty]
    private double _selectedNodeXMm;

    [ObservableProperty]
    private double _selectedNodeYMm;

    [ObservableProperty]
    private double _selectedNodeWidthMm = 40.0;

    [ObservableProperty]
    private double _selectedNodeHeightMm = 40.0;

    [ObservableProperty]
    private double _selectedNodeRotationDeg;

    [ObservableProperty]
    private double _selectedNodeOpacityPercent = 100.0;

    [ObservableProperty]
    private Color _selectedShapeStrokeColor = Color.FromRgb(15, 23, 42);

    [ObservableProperty]
    private double _selectedShapeStrokeWidthMm = 1.0;

    [ObservableProperty]
    private MaskOption? _selectedMaskOption;

    [ObservableProperty]
    private FilterPresetOption? _selectedFilterPresetOption;

    [ObservableProperty]
    private bool _selectedTextBold;

    [ObservableProperty]
    private TextCurveOption? _selectedTextCurveOption;

    [ObservableProperty]
    private double _selectedTextCurveBend = 0.65;

    [ObservableProperty]
    private double _selectedTextLetterSpacingEm;

    [ObservableProperty]
    private bool _selectedTextHasOutline;

    [ObservableProperty]
    private Color _selectedTextOutlineColor = Colors.White;

    [ObservableProperty]
    private double _selectedTextOutlineWidthMm = 0.35;

    [ObservableProperty]
    private bool _selectedTextHasPill;

    [ObservableProperty]
    private Color _selectedTextPillColor = Color.FromRgb(17, 107, 54);

    [ObservableProperty]
    private bool _selectedTextHasShadow;

    public IReadOnlyList<TextCurveOption> AvailableTextCurveOptions { get; } =
    [
        new(TextCurveKind.None, "مستقيم (عادي)"),
        new(TextCurveKind.ArcUp, "قوس علوي ◠"),
        new(TextCurveKind.ArcDown, "قوس سفلي ◡"),
        new(TextCurveKind.FullCircle, "دائري كامل ◯"),
    ];

    public bool IsSingleNodeSelected => SelectedNodes.Count == 1;

    public bool IsTextAlignRight => SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode { Alignment: TextAlignmentKind.Right };
    public bool IsTextAlignCenter => SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode { Alignment: TextAlignmentKind.Center };
    public bool IsTextAlignLeft => SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode { Alignment: TextAlignmentKind.Left };
    public bool HasSelectedTextCurve => IsTextSelected && SelectedTextCurveOption?.Kind is { } k && k != TextCurveKind.None;

    public bool HasNoFreeformLayers => LayerItems.Count == 0;
    public string LayerCountBadgeText => LayerItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public IBrush StudioPrimaryBrush => new SolidColorBrush(ToAvaloniaColor(CurrentStickerPalette.Primary));
    public IBrush StudioSecondaryBrush => new SolidColorBrush(ToAvaloniaColor(CurrentStickerPalette.Secondary));
    public IBrush StudioAccentBrush => new SolidColorBrush(ToAvaloniaColor(CurrentStickerPalette.Accent));
    public IBrush StudioSurfaceBrush => new SolidColorBrush(ToAvaloniaColor(CurrentStickerPalette.SurfaceBackground));

    public string SelectedStickerTemplateTitle => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode st
        ? Domain.Stickers.ParametricStickerCatalog.ResolveTemplate(st.TemplateId).TitleAr
        : string.Empty;

    public IBrush SelectedStickerPrimaryBrush => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode st
        ? new SolidColorBrush(ToAvaloniaColor(st.Palette.Primary))
        : Brushes.Transparent;

    public IBrush SelectedStickerSecondaryBrush => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode st
        ? new SolidColorBrush(ToAvaloniaColor(st.Palette.Secondary))
        : Brushes.Transparent;

    public IBrush SelectedStickerAccentBrush => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode st
        ? new SolidColorBrush(ToAvaloniaColor(st.Palette.Accent))
        : Brushes.Transparent;

    public IBrush SelectedStickerSurfaceBrush => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode st
        ? new SolidColorBrush(ToAvaloniaColor(st.Palette.SurfaceBackground))
        : Brushes.Transparent;

    private void SyncExtendedInspectorFields()
    {
        _isSyncingInspectorFields = true;
        try
        {
            if (SelectedNodes.Count == 1)
            {
                SceneNode single = SelectedNodes[0];
                if (_syncedTransformNodeId != single.Id ||
                    !Equals(_syncedTransformBounds, single.BoundsMm) ||
                    Math.Abs(_syncedTransformRotation - single.RotationDeg) > 0.0001)
                {
                    _syncedTransformNodeId = single.Id;
                    _syncedTransformBounds = single.BoundsMm;
                    _syncedTransformRotation = single.RotationDeg;
                    SelectedNodeXMm = Math.Round(single.BoundsMm.X, 1);
                    SelectedNodeYMm = Math.Round(single.BoundsMm.Y, 1);
                    SelectedNodeWidthMm = Math.Round(single.BoundsMm.Width, 1);
                    SelectedNodeHeightMm = Math.Round(single.BoundsMm.Height, 1);
                    SelectedNodeRotationDeg = Math.Round(((single.RotationDeg % 360.0) + 360.0) % 360.0, 1);
                }

                SelectedNodeOpacityPercent = Math.Round(single.Opacity * 100.0);

                if (single is ShapeNode shape)
                {
                    if (shape.Stroke is { } stroke)
                    {
                        SelectedShapeStrokeColor = ToAvaloniaColor(stroke.Color);
                        SelectedShapeStrokeWidthMm = Math.Round(stroke.WidthMm, 1);
                    }
                    else
                    {
                        SelectedShapeStrokeWidthMm = 0.0;
                    }
                }
                else if (single is TextNode txt)
                {
                    SelectedTextBold = txt.FontWeight >= 600;
                    TextCurveKind ck = txt.Curve?.Kind ?? TextCurveKind.None;
                    SelectedTextCurveOption = AvailableTextCurveOptions.FirstOrDefault(o => o.Kind == ck)
                        ?? AvailableTextCurveOptions[0];
                    SelectedTextCurveBend = Math.Round(txt.Curve?.BendFactor ?? 0.65, 2);
                    SelectedTextLetterSpacingEm = Math.Round(txt.LetterSpacingEm, 2);

                    TextEffectSpec fx = txt.EffectiveEffects;
                    SelectedTextHasOutline = fx.Outline is { WidthMm: > 0.0 };
                    if (fx.Outline is { } outline)
                    {
                        SelectedTextOutlineColor = ToAvaloniaColor(outline.Color);
                        SelectedTextOutlineWidthMm = Math.Round(outline.WidthMm, 2);
                    }

                    SelectedTextHasPill = fx.PillBackgroundColor is { A: > 0 };
                    if (fx.PillBackgroundColor is { } pill)
                    {
                        SelectedTextPillColor = ToAvaloniaColor(pill);
                    }

                    SelectedTextHasShadow = fx.Shadow is { Color.A: > 0 };
                }
            }
            else
            {
                _syncedTransformNodeId = null;
            }

            SelectedMaskOption = AvailableMaskOptions.FirstOrDefault(m => m.Kind == SelectedImageMask)
                ?? AvailableMaskOptions[0];
            SelectedFilterPresetOption = AvailableFilterPresetOptions.FirstOrDefault(p => p.Preset == SelectedFilterPreset)
                ?? AvailableFilterPresetOptions[0];

            HashSet<NodeId> selectedSet = _session.SelectedNodeIds.ToHashSet();
            LayerItems.Clear();
            for (int i = ActivePage.Nodes.Length - 1; i >= 0; i--)
            {
                SceneNode n = ActivePage.Nodes[i];
                LayerItems.Add(new SceneLayerItemViewModel(
                    n.Id,
                    n.Name,
                    ResolveNodeKindBadge(n),
                    $"{n.BoundsMm.Width:0.#}×{n.BoundsMm.Height:0.#} mm",
                    n.IsVisible,
                    n.IsLocked,
                    selectedSet.Contains(n.Id)));
            }
        }
        finally
        {
            _isSyncingInspectorFields = false;
        }

        OnPropertyChanged(nameof(IsSingleNodeSelected));
        OnPropertyChanged(nameof(IsTextAlignRight));
        OnPropertyChanged(nameof(IsTextAlignCenter));
        OnPropertyChanged(nameof(IsTextAlignLeft));
        OnPropertyChanged(nameof(HasSelectedTextCurve));
        OnPropertyChanged(nameof(SelectedStickerTemplateTitle));
        OnPropertyChanged(nameof(SelectedStickerPrimaryBrush));
        OnPropertyChanged(nameof(SelectedStickerSecondaryBrush));
        OnPropertyChanged(nameof(SelectedStickerAccentBrush));
        OnPropertyChanged(nameof(SelectedStickerSurfaceBrush));
        OnPropertyChanged(nameof(HasNoFreeformLayers));
        OnPropertyChanged(nameof(LayerCountBadgeText));
    }

    private static string ResolveNodeKindBadge(SceneNode node) => node switch
    {
        ImageNode => "صورة",
        TextNode => "نص",
        ShapeNode => "شكل",
        SvgNode => "شعار",
        StickerNode => "ملصق",
        GroupNode => "مجموعة",
        _ => "عنصر",
    };

    partial void OnSelectedMaskOptionChanged(MaskOption? value)
    {
        if (!_isSyncingInspectorFields && value is not null)
        {
            SelectedImageMask = value.Kind;
        }
    }

    partial void OnSelectedFilterPresetOptionChanged(FilterPresetOption? value)
    {
        if (_isSyncingInspectorFields || value is null)
        {
            return;
        }

        SelectedFilterPreset = value.Preset;
        if (IsPhotoToolsVisible)
        {
            ApplyImageFilters();
        }
    }

    partial void OnSelectedNodeOpacityPercentChanged(double value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1)
        {
            return;
        }

        SceneNode target = SelectedNodes[0];
        double op = Math.Clamp(value / 100.0, 0.05, 1.0);
        _session.ApplyCommand(
            "تغيير شفافية العنصر",
            doc => FreeformSceneOperations.SetNodeTransform(doc, target.Id, opacity: op),
            coalesceKey: "node.opacity");
    }

    partial void OnSelectedShapeStrokeColorChanged(Color value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not ShapeNode shape)
        {
            return;
        }

        ColorRgba rgba = ToColorRgba(value);
        double width = Math.Max(0.4, SelectedShapeStrokeWidthMm);
        _session.ApplyCommand(
            "تغيير لون حدود الشكل",
            doc => FreeformSceneOperations.UpdateShapeNodeStyle(doc, shape.Id, strokeColor: rgba, strokeWidthMm: width),
            coalesceKey: "shape.stroke.color");
    }

    partial void OnSelectedTextFontSizePtChanged(double value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt || !double.IsFinite(value))
        {
            return;
        }

        _session.ApplyCommand(
            "تغيير حجم الخط",
            doc => FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, fontSizePt: value),
            coalesceKey: "text.size");
    }

    partial void OnSelectedTextFontFamilyChanged(string value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _session.ApplyCommand(
            "تغيير نوع الخط",
            doc => FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, fontFamily: value),
            coalesceKey: "text.family");
    }

    [RelayCommand]
    private void ApplyNumericTransform()
    {
        if (SelectedNodes.Count != 1)
        {
            return;
        }

        SceneNode single = SelectedNodes[0];
        var nextRect = new RectMm(
            SelectedNodeXMm,
            SelectedNodeYMm,
            Math.Max(2.0, SelectedNodeWidthMm),
            Math.Max(2.0, SelectedNodeHeightMm));
        double opacity = Math.Clamp(SelectedNodeOpacityPercent / 100.0, 0.05, 1.0);
        _session.ApplyCommand(
            "ضبط الأبعاد والموقع",
            doc => FreeformSceneOperations.SetNodeTransform(doc, single.Id, nextRect, SelectedNodeRotationDeg, opacity));
        StatusMessage = "تم تحديث موقع وأبعاد العنصر بالمليمتر";
    }

    [RelayCommand]
    private void AlignSelectionLeft() => AlignSelectedNodes(PageAlignmentKind.Left, "محاذاة لليسار");

    [RelayCommand]
    private void AlignSelectionCenterH() => AlignSelectedNodes(PageAlignmentKind.CenterH, "توسيط أفقي");

    [RelayCommand]
    private void AlignSelectionRight() => AlignSelectedNodes(PageAlignmentKind.Right, "محاذاة لليمين");

    [RelayCommand]
    private void AlignSelectionTop() => AlignSelectedNodes(PageAlignmentKind.Top, "محاذاة للأعلى");

    [RelayCommand]
    private void AlignSelectionCenterV() => AlignSelectedNodes(PageAlignmentKind.CenterV, "توسيط رأسي");

    [RelayCommand]
    private void AlignSelectionBottom() => AlignSelectedNodes(PageAlignmentKind.Bottom, "محاذاة للأسفل");

    private void AlignSelectedNodes(PageAlignmentKind kind, string descriptionAr)
    {
        if (!HasSelection)
        {
            return;
        }

        var ids = _session.SelectedNodeIds;
        if (_session.ApplyCommand(descriptionAr, doc => FreeformSceneOperations.AlignSelectionToPage(doc, ids, kind)))
        {
            StatusMessage = $"تم تنفيذ: {descriptionAr}";
        }
    }

    [RelayCommand]
    private void AlignTextRight() => ApplyTextAlignment(TextAlignmentKind.Right);

    [RelayCommand]
    private void AlignTextCenter() => ApplyTextAlignment(TextAlignmentKind.Center);

    [RelayCommand]
    private void AlignTextLeft() => ApplyTextAlignment(TextAlignmentKind.Left);

    private void ApplyTextAlignment(TextAlignmentKind alignment)
    {
        if (SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt)
        {
            return;
        }

        _session.ApplyCommand("تغيير محاذاة النص", doc =>
            FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, alignment: alignment));
    }

    partial void OnSelectedTextBoldChanged(bool value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt)
        {
            return;
        }

        int targetWeight = value ? 700 : 400;
        if (txt.FontWeight != targetWeight)
        {
            _session.ApplyCommand("تغيير سُمك الخط", doc =>
                FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, fontWeight: targetWeight));
        }
    }

    partial void OnSelectedTextCurveOptionChanged(TextCurveOption? value)
    {
        OnPropertyChanged(nameof(HasSelectedTextCurve));
        if (_isSyncingInspectorFields || value is null || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt)
        {
            return;
        }

        TextCurveKind currentKind = txt.Curve?.Kind ?? TextCurveKind.None;
        if (currentKind != value.Kind)
        {
            double bend = Math.Clamp(SelectedTextCurveBend, 0.2, 1.0);
            _session.ApplyCommand("تغيير انحناء النص", doc =>
                FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, curveKind: value.Kind, curveBendFactor: bend));
        }
    }

    partial void OnSelectedTextCurveBendChanged(double value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt ||
            txt.Curve is not { Kind: not TextCurveKind.None } || !double.IsFinite(value))
        {
            return;
        }

        _session.ApplyCommand(
            "تعديل شدة انحناء النص",
            doc => FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, curveBendFactor: value),
            coalesceKey: "text.curve.bend");
    }

    partial void OnSelectedTextLetterSpacingEmChanged(double value)
    {
        if (_isSyncingInspectorFields || SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt || !double.IsFinite(value))
        {
            return;
        }

        _session.ApplyCommand(
            "تعديل تباعد أحرف النص",
            doc => FreeformSceneOperations.UpdateTextNodeStyle(doc, txt.Id, letterSpacingEm: value),
            coalesceKey: "text.spacing");
    }

    partial void OnSelectedTextHasOutlineChanged(bool value)
    {
        if (!_isSyncingInspectorFields)
        {
            ApplySelectedTextEffects();
        }
    }

    partial void OnSelectedTextHasPillChanged(bool value)
    {
        if (!_isSyncingInspectorFields)
        {
            ApplySelectedTextEffects();
        }
    }

    partial void OnSelectedTextHasShadowChanged(bool value)
    {
        if (!_isSyncingInspectorFields)
        {
            ApplySelectedTextEffects();
        }
    }

    [RelayCommand]
    private void ApplySelectedTextEffects()
    {
        if (SelectedNodes.Count != 1 || SelectedNodes[0] is not TextNode txt)
        {
            return;
        }

        StrokeSpec? outline = SelectedTextHasOutline
            ? new StrokeSpec(ToColorRgba(SelectedTextOutlineColor), Math.Clamp(SelectedTextOutlineWidthMm, 0.1, 5.0))
            : null;
        ColorRgba? pill = SelectedTextHasPill ? ToColorRgba(SelectedTextPillColor) : null;
        ShadowSpec? shadow = SelectedTextHasShadow
            ? new ShadowSpec(new ColorRgba(15, 23, 42, 70), 0.4, 0.8, 1.4)
            : null;

        TextEffectSpec? nextEffects = outline is null && pill is null && shadow is null
            ? null
            : new TextEffectSpec(
                Outline: outline,
                PillBackgroundColor: pill,
                PillPaddingMm: txt.Effects?.PillPaddingMm ?? 2.2,
                PillCornerRadiusMm: txt.Effects?.PillCornerRadiusMm ?? 4.5,
                Shadow: shadow);

        _session.ApplyCommand("تحديث تأثيرات النص", doc =>
            FreeformSceneOperations.UpdateTextNodeStyle(
                doc,
                txt.Id,
                effects: nextEffects,
                updateEffects: true));
        StatusMessage = "تم تحديث تأثيرات النص";
    }

    [RelayCommand]
    private void SelectLayer(SceneLayerItemViewModel? layer)
    {
        if (layer is not { CanSelectOnCanvas: true })
        {
            return;
        }

        _session.SetSelection([layer.Id]);
    }

    [RelayCommand]
    private void ToggleLayerVisibility(SceneLayerItemViewModel? layer)
    {
        if (layer is null)
        {
            return;
        }

        _session.ApplyCommand(
            layer.IsVisible ? "إخفاء طبقة" : "إظهار طبقة",
            doc => FreeformSceneOperations.ToggleNodeVisibility(doc, layer.Id));
    }

    [RelayCommand]
    private void ToggleLayerLock(SceneLayerItemViewModel? layer)
    {
        if (layer is null)
        {
            return;
        }

        _session.ApplyCommand(
            layer.IsLocked ? "فتح قفل طبقة" : "قفل طبقة",
            doc => FreeformSceneOperations.ToggleNodeLock(doc, layer.Id));
    }
}
