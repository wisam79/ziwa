using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Application.Auth;
using Ziwa.Application.Bridge;
using Ziwa.Application.Collage;
using Ziwa.Application.Commands;
using Ziwa.Application.Interaction;
using Ziwa.Application.Pages;
using Ziwa.Application.Persistence;
using Ziwa.Application.PrePress;
using Ziwa.Application.Printing;
using Ziwa.Application.Scanner;
using Ziwa.Application.Session;
using Ziwa.Application.Stickers;
using Ziwa.Application.Typography;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Rendering;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Viewport;

namespace Ziwa.App.ViewModels;

public sealed partial class StickerFieldViewModel : ObservableObject
{
    public StickerFieldViewModel(string key, string labelAr, string value)
    {
        Key = key;
        LabelAr = labelAr;
        _value = value;
    }

    public string Key { get; }
    public string LabelAr { get; }

    [ObservableProperty]
    private string _value;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DocumentSession _session;
    private readonly CommandRegistry _commandRegistry;
    private readonly IPageExportService _exportService;
    private readonly IFreeformToCollageService _transferService;
    private readonly IPlatformCapabilities _platformCapabilities;
    private readonly StudioBrandKit _brandKit;
    private readonly IAssetRegistrar? _assetRegistrar;
    private readonly IPrintSpooler? _printSpooler;

    private string? _internalClipboard;
    private bool _autoOpenedInspector;
    private int _paletteSeed = 42;
    private NodeId? _editingStickerTargetId;
    private bool _isSyncingStickerStudio;
    private SKBitmap? _stickerPreviewBitmap;

    [ObservableProperty]
    private ViewportTransform _viewport = new(1.0, 32.0, 32.0, 1.0);

    [ObservableProperty]
    private bool _isDarkTheme = true;

    [ObservableProperty]
    private string _statusMessage = "جاهز — محرك SkiaSharp + HarfBuzz يعمل بدقة المليمتر";

    [ObservableProperty]
    private string _selectedTextContent = string.Empty;

    [ObservableProperty]
    private MaskShapeKind _selectedImageMask = MaskShapeKind.None;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    [ObservableProperty]
    private bool _isZenMode;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isStickerStudioOpen;

    [ObservableProperty]
    private StickerTemplateDefinition _selectedStickerTemplate = ParametricStickerCatalog.CommercialSeal;

    [ObservableProperty]
    private StickerPalette _currentStickerPalette = ParametricStickerCatalog.CommercialSeal.DefaultPalette;

    [ObservableProperty]
    private Color _stickerCustomPrimaryColor = Color.FromRgb(27, 83, 148);

    public SKBitmap? StickerPreviewBitmap => _stickerPreviewBitmap;

    [ObservableProperty]
    private double _filterBrightness;

    [ObservableProperty]
    private double _filterContrast;

    [ObservableProperty]
    private double _filterSaturation;

    [ObservableProperty]
    private PhotoFilterPreset _selectedFilterPreset = PhotoFilterPreset.None;

    [ObservableProperty]
    private bool _isPrintDialogOpen;

    [ObservableProperty]
    private PrinterDeviceInfo? _selectedPrinter;

    [ObservableProperty]
    private PrintOrientationKind _selectedPrintOrientation = PrintOrientationKind.Portrait;

    [ObservableProperty]
    private int _printCopies = 1;

    [ObservableProperty]
    private bool _printBorderless;

    [ObservableProperty]
    private bool _printIncludeBleed;

    [ObservableProperty]
    private bool _printIncludeCropMarks;

    [ObservableProperty]
    private bool _printIncludeRegistrationMarks;

    [ObservableProperty]
    private bool _printIncludeCutContour;

    [ObservableProperty]
    private bool _printAllPages = true;

    [ObservableProperty]
    private CmykProfileKind _selectedCmykProfile = CmykProfileKind.CoatedFogra39;

    [ObservableProperty]
    private string _printCalibrationSummaryText = string.Empty;

    [ObservableProperty]
    private bool _hasHardwareMarginClipWarning;

    [ObservableProperty]
    private int? _lastSpoolJobId;

    [ObservableProperty]
    private string? _lastSpoolPrinterName;

    public MainWindowViewModel(
        DocumentSession session,
        CommandRegistry commandRegistry,
        SceneRenderer sceneRenderer,
        IPageExportService exportService,
        IFreeformToCollageService transferService,
        StudioBrandKit brandKit,
        IPlatformCapabilities platformCapabilities,
        ProjectWorkspace projects,
        IAssetRegistrar? assetRegistrar = null,
        IPrintSpooler? printSpooler = null,
        OfflineAiCoordinator? aiCoordinator = null,
        IImageAssetImporter? imageImporter = null,
        AssetStore? assetStore = null,
        IProjectDialogs? dialogs = null,
        DocumentScannerCoordinator? scannerCoordinator = null,
        CloudAiCoordinator? cloudAiCoordinator = null,
        AccountSessionCoordinator? accountCoordinator = null,
        PhoneBridgeCoordinator? phoneBridgeCoordinator = null,
        IStructuredAppLogger? appLogger = null,
        IAppUpdaterService? appUpdater = null,
        IDropFileFilter? dropFileFilter = null)
    {
        _session = session;
        _commandRegistry = commandRegistry;
        SceneRenderer = sceneRenderer;
        _exportService = exportService;
        _transferService = transferService;
        _brandKit = brandKit;
        _platformCapabilities = platformCapabilities;
        _assetRegistrar = assetRegistrar;
        _printSpooler = printSpooler;
        _projects = projects;
        _projects.Changed += OnProjectsChanged;

        _session.SessionChanged += OnSessionChanged;
        RegisterUnifiedCommands();
        RegisterProjectCommands();
        InitializeAiIntegration(aiCoordinator, imageImporter, assetStore, dialogs, cloudAiCoordinator);
        InitializeAccountIntegration(accountCoordinator);
        InitializeScannerIntegration(scannerCoordinator);
        InitializePhoneBridgeIntegration(phoneBridgeCoordinator);
        InitializeDiagnosticsAndUpdateIntegration(appLogger, appUpdater, dropFileFilter);
        LoadStickerStudioFromTemplate(ParametricStickerCatalog.CommercialSeal, existingFields: null, palette: null);
        RefreshPrinters();
        RefreshFromSession();
        RefreshFilteredCommands();
    }

    public DocumentSession Session => _session;
    public CommandRegistry CommandRegistry => _commandRegistry;
    public ZiwaDocument Document => _session.EffectiveDocument;
    public PageModel ActivePage => Document.ActivePage;
    public SceneRenderer SceneRenderer { get; }
    public ObservableCollection<string> NodeSummaries { get; } = [];
    public ObservableCollection<CommandDefinition> FilteredCommands { get; } = [];
    public ObservableCollection<StickerFieldViewModel> StickerFieldItems { get; } = [];
    public ObservableCollection<PageTabItemViewModel> PageTabs { get; } = [];
    public ObservableCollection<PreflightIssue> PreflightIssues { get; } = [];
    public ObservableCollection<PrinterDeviceInfo> AvailablePrinters { get; } = [];

    public IReadOnlyList<MaskShapeKind> AvailableMasks { get; } = Enum.GetValues<MaskShapeKind>();
    public IReadOnlyList<PhotoFilterPreset> AvailableFilterPresets { get; } = Enum.GetValues<PhotoFilterPreset>();
    public IReadOnlyList<StickerTemplateDefinition> StickerTemplates { get; } = ParametricStickerCatalog.ExtendedAll;
    public IReadOnlyList<PrintOrientationKind> AvailableOrientations { get; } = Enum.GetValues<PrintOrientationKind>();
    public IReadOnlyList<CmykProfileKind> AvailableCmykProfiles { get; } = Enum.GetValues<CmykProfileKind>();

    public string DocumentTitle => Document.Title;
    public string PageName => ActivePage.Name;
    public double PageWidthMm => ActivePage.WidthMm;
    public double PageHeightMm => ActivePage.HeightMm;
    public string PageSizeMmText => $"{ActivePage.WidthMm:0.#} × {ActivePage.HeightMm:0.#} mm";
    public string EffectiveDpiText => $"{Document.PrintSettings.ExportDpi:0} DPI";
    public string PlatformStatusText => _platformCapabilities.PlatformName;
    public string ZoomPercentText => $"{Viewport.Zoom * 100.0:0}%";
    public string ThemeToggleText => IsDarkTheme ? "المظهر الفاتح" : "المظهر الداكن";
    public string ActivePageCounterText => $"{Document.ActivePageIndex + 1} / {Document.Pages.Length}";
    public string ActivePageDisplayText => $"صفحة {Document.ActivePageIndex + 1} من {Document.Pages.Length} — {ActivePage.Name}";
    public bool IsMultiPage => Document.Pages.Length > 1;
    public bool CanDeleteActivePage => Document.Pages.Length > 1;
    public bool CanMovePageEarlier => Document.ActivePageIndex > 0;
    public bool CanMovePageLater => Document.ActivePageIndex < Document.Pages.Length - 1;
    public bool HasPreflightWarnings => PreflightIssues.Count > 0;
    public string PreflightSummaryText => $"تنبيهات ما قبل الطباعة ({PreflightIssues.Count})";
    public bool CanCancelLastPrintJob => LastSpoolJobId is > 0 && !string.IsNullOrWhiteSpace(LastSpoolPrinterName);

    public bool CanUndo => _session.CanUndo;
    public bool CanRedo => _session.CanRedo;
    public bool HasSelection => !_session.SelectedNodeIds.IsDefaultOrEmpty;
    public bool IsCollageMode => ActivePage.CollageLayout is not null;
    public bool IsCollageSlotSelected => _session.SelectedCollageSlotId is not null;
    public bool HasAnySelection => HasSelection || IsCollageSlotSelected;
    public bool CanGroup => _session.SelectedNodeIds.Length >= 2;
    public bool CanUngroup => SelectedNodes.Any(n => n is GroupNode);
    public bool IsTextSelected => SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode;
    public bool IsImageSelected => SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode;
    public bool IsSvgSelected => SelectedNodes.Count == 1 && SelectedNodes[0] is SvgNode;
    public bool IsStickerSelected => SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode;
    public bool IsPhotoToolsVisible => (IsImageSelected || IsCollageSlotSelected) && !IsStickerSelected;
    public bool IsQuickBarVisible => HasAnySelection && !IsZenMode;
    public bool IsChromeVisible => !IsZenMode;
    public bool EffectiveInspectorVisible => !IsZenMode && IsInspectorOpen;
    public bool CanShowReopenInspectorButton => !IsZenMode && !IsInspectorOpen;
    public string WorkspaceModeBadgeText => IsCollageMode ? "شيت كولاج" : "وضع حر";
    public bool HasNoFilteredCommands => FilteredCommands.Count == 0;

    public bool CanMaskImageWithShape =>
        SelectedNodes.Count == 2 &&
        SelectedNodes.OfType<ImageNode>().Any() &&
        SelectedNodes.OfType<ShapeNode>().Any(s => s.Kind != ShapeKind.Line);

    public string SelectionSummary
    {
        get
        {
            if (IsCollageSlotSelected && SelectedCollageSlot is { } slot)
            {
                return $"خانة كولاج #{slot.Index + 1} ({slot.BoundsMm.Width:0.#}×{slot.BoundsMm.Height:0.#} mm)";
            }

            return SelectedNodes.Count switch
            {
                0 => "لا يوجد عنصر محدد",
                1 => SelectedNodes[0].Name,
                _ => $"{SelectedNodes.Count} عناصر محددة",
            };
        }
    }

    private CollageSlot? SelectedCollageSlot =>
        _session.SelectedCollageSlotId is { } sid
            ? ActivePage.CollageLayout?.Slots.FirstOrDefault(s => s.Id == sid)
            : null;

    private IReadOnlyList<SceneNode> SelectedNodes
    {
        get
        {
            if (_session.SelectedNodeIds.IsDefaultOrEmpty)
            {
                return [];
            }

            HashSet<NodeId> idSet = _session.SelectedNodeIds.ToHashSet();
            return ActivePage.Nodes.Where(n => idSet.Contains(n.Id)).ToList();
        }
    }

    private void RegisterUnifiedCommands()
    {
        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.undo"),
            "تراجع",
            "Ctrl+Z",
            "تحرير",
            () => _session.CanUndo,
            _ =>
            {
                if (_session.Undo())
                {
                    StatusMessage = "تم التراجع عن آخر خطوة";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.redo"),
            "إعادة",
            "Ctrl+Y",
            "تحرير",
            () => _session.CanRedo,
            _ =>
            {
                if (_session.Redo())
                {
                    StatusMessage = "تمت إعادة الخطوة";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.copy"),
            "نسخ التحديد",
            "Ctrl+C",
            "تحرير",
            () => HasSelection,
            _ =>
            {
                if (SelectedNodes.Count > 0)
                {
                    _internalClipboard = ClipboardPayloadSerializer.SerializeNodes(SelectedNodes);
                    StatusMessage = $"تم نسخ {SelectedNodes.Count} عنصر";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.paste"),
            "لصق",
            "Ctrl+V",
            "تحرير",
            () => !string.IsNullOrEmpty(_internalClipboard),
            _ =>
            {
                if (ClipboardPayloadSerializer.TryDeserializeAndClone(_internalClipboard, out ImmutableArray<SceneNode> clones))
                {
                    _session.ApplyCommand("لصق عناصر", doc =>
                    {
                        ZiwaDocument current = doc;
                        foreach (SceneNode clone in clones)
                        {
                            current = FreeformSceneOperations.AddNode(current, clone);
                        }

                        return current;
                    });
                    _session.SetSelection(clones.Select(c => c.Id).ToImmutableArray());
                    StatusMessage = $"تم لصق {clones.Length} عنصر بأمان";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.delete"),
            "حذف التحديد",
            "Delete",
            "تحرير",
            () => HasAnySelection,
            _ =>
            {
                if (IsCollageSlotSelected && _session.SelectedCollageSlotId is { } sid)
                {
                    _session.ApplyCommand("تفريغ خانة الكولاج", doc => CollageOperations.ClearSlot(doc, sid));
                    StatusMessage = "تم تفريغ خانة الكولاج";
                    return Task.CompletedTask;
                }

                HashSet<NodeId> ids = _session.SelectedNodeIds.ToHashSet();
                if (_session.ApplyCommand("حذف التحديد", doc => FreeformSceneOperations.DeleteNodes(doc, ids)))
                {
                    StatusMessage = "تم حذف العناصر المحددة";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("arrange.group"),
            "تجميع",
            "Ctrl+G",
            "ترتيب",
            () => CanGroup,
            _ =>
            {
                NodeId groupId = NodeId.Empty;
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                if (_session.ApplyCommand("تجميع العناصر", doc => FreeformSceneOperations.GroupNodes(doc, ids, out groupId)))
                {
                    _session.SetSelection([groupId]);
                    StatusMessage = "تم تجميع العناصر";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("arrange.ungroup"),
            "فك التجميع",
            "Ctrl+Shift+G",
            "ترتيب",
            () => CanUngroup,
            _ =>
            {
                ImmutableArray<NodeId> restored = [];
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                if (_session.ApplyCommand("فك التجميع", doc => FreeformSceneOperations.UngroupNodes(doc, ids, out restored)))
                {
                    _session.SetSelection(restored);
                    StatusMessage = "تم فك التجميع";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("image.maskWithShape"),
            "قنّع الصورة بهذا الشكل",
            string.Empty,
            "أدوات",
            () => CanMaskImageWithShape,
            _ =>
            {
                NodeId resultId = NodeId.Empty;
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                if (_session.ApplyCommand("تقنيع الصورة بالشكل", doc => FreeformSceneOperations.MaskSelectedImageWithShape(doc, ids, out resultId)))
                {
                    _session.SetSelection([resultId]);
                    StatusMessage = "تم دمج الصورة داخل الشكل";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("image.restoreOriginal"),
            "استعادة الأصل",
            string.Empty,
            "صور",
            () => CanRestoreOriginalTarget,
            _ =>
            {
                if (IsCollageSlotSelected && _session.SelectedCollageSlotId is { } sid)
                {
                    _session.ApplyCommand("استعادة صورة الخانة الأصلية", doc => CollageOperations.RestoreOriginalSlotImage(doc, sid));
                    StatusMessage = "تمت استعادة الصورة الأصلية للخانة";
                }
                else if (SelectedNodes.FirstOrDefault() is ImageNode img)
                {
                    _session.ApplyCommand("استعادة الصورة الأصلية", doc => FreeformSceneOperations.RestoreOriginalImage(doc, img.Id));
                    StatusMessage = "تمت استعادة الصورة الأصلية";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.headingText"),
            "إدراج عنوان عربي",
            string.Empty,
            "إدراج",
            () => true,
            _ =>
            {
                TextNode heading = TypographyPresets.CreateHeading();
                _session.ApplyCommand("إدراج عنوان", doc => FreeformSceneOperations.AddNode(doc, heading));
                _session.SetSelection([heading.Id]);
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
        new CommandId("insert.curvedSeal"),
        "إدراج ختم عربي منحنٍ",
        string.Empty,
        "إدراج",
        () => true,
        _ =>
        {
            TextNode seal = TypographyPresets.CreateCurvedSeal();
            _session.ApplyCommand("إدراج ختم منحنٍ", doc => FreeformSceneOperations.AddNode(doc, seal));
            _session.SetSelection([seal.Id]);
            return Task.CompletedTask;
        }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.subheadingText"),
            "إدراج نص فرعي",
            string.Empty,
            "إدراج",
            () => true,
            _ =>
            {
                TextNode sub = TypographyPresets.CreateSubheading();
                _session.ApplyCommand("إدراج نص فرعي", doc => FreeformSceneOperations.AddNode(doc, sub));
                _session.SetSelection([sub.Id]);
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.capsuleBadge"),
            "إدراج شارة نصية كبسولة",
            string.Empty,
            "إدراج",
            () => true,
            _ =>
            {
                TextNode badge = TypographyPresets.CreateCapsuleBadge();
                _session.ApplyCommand("إدراج شارة كبسولة", doc => FreeformSceneOperations.AddNode(doc, badge));
                _session.SetSelection([badge.Id]);
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.watermark"),
            "إدراج علامة مائية مائلة",
            string.Empty,
            "إدراج",
            () => true,
            _ =>
            {
                TextNode wm = TypographyPresets.CreateWatermark();
                _session.ApplyCommand("إدراج علامة مائية", doc => FreeformSceneOperations.AddNode(doc, wm));
                _session.SetSelection([wm.Id]);
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.starShape"),
            "إدراج نجمة",
            string.Empty,
            "إدراج",
            () => true,
            _ => InsertDefaultShape(
                ShapeKind.Star,
                "نجمة خماسية",
                new RectMm(54.0, 95.0, 40.0, 40.0),
                new SolidShapeFill(new ColorRgba(242, 204, 96, 255)),
                new StrokeSpec(new ColorRgba(138, 75, 0, 255), 0.6),
                polygonSides: 5,
                innerRatio: 0.44)));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.rectangleShape"),
            "إدراج مستطيل",
            string.Empty,
            "إدراج",
            () => true,
            _ => InsertDefaultShape(
                ShapeKind.Rectangle,
                "مستطيل",
                new RectMm(48.0, 88.0, 56.0, 36.0),
                new SolidShapeFill(new ColorRgba(11, 92, 171, 255)),
                new StrokeSpec(new ColorRgba(8, 56, 108, 255), 0.4))));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.ellipseShape"),
            "إدراج دائرة / بيضاوي",
            string.Empty,
            "إدراج",
            () => true,
            _ => InsertDefaultShape(
                ShapeKind.Ellipse,
                "دائرة",
                new RectMm(54.0, 88.0, 42.0, 42.0),
                new SolidShapeFill(new ColorRgba(16, 148, 120, 255)),
                new StrokeSpec(new ColorRgba(10, 96, 76, 255), 0.4))));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.lineShape"),
            "إدراج خط مستقيم",
            string.Empty,
            "إدراج",
            () => true,
            _ => InsertDefaultShape(
                ShapeKind.Line,
                "خط فاصل",
                new RectMm(40.0, 110.0, 70.0, 4.0),
                new SolidShapeFill(ColorRgba.Transparent),
                new StrokeSpec(new ColorRgba(30, 41, 59, 255), 1.0))));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("sticker.openStudio"),
            "استوديو الملصقات البارامتري",
            string.Empty,
            "ملصقات",
            () => true,
            _ =>
            {
                OpenNewStickerStudio();
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("svg.toggleTint"),
            "تلوين الشعار",
            string.Empty,
            "SVG",
            () => IsSvgSelected,
            _ =>
            {
                if (SelectedNodes.FirstOrDefault() is SvgNode svg)
                {
                    ColorRgba? nextTint = svg.TintColor is null ? new ColorRgba(180, 35, 24, 255) : null;
                    _session.ApplyCommand("تلوين شعار SVG", doc => FreeformSceneOperations.SetSvgTint(doc, svg.Id, nextTint));
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("quickstart.idPhotoSheet"),
            "بدء سريع: شيت صور هوية 4×6",
            "Alt+1",
            "بداية سريعة",
            () => true,
            async ct =>
            {
                AssetId? sampleAsset = Document.Pages.SelectMany(p => p.Nodes).OfType<ImageNode>().FirstOrDefault()?.OriginalAssetId;
                if (await _projects.ReplaceAsync(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6, sampleAsset), ct))
                {
                    StatusMessage = "تم فتح شيت صور هوية 4×6 بوصة";
                }
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("quickstart.businessCard"),
            "بدء سريع: بطاقة عمل",
            "Alt+2",
            "بداية سريعة",
            () => true,
            async ct =>
            {
                if (await _projects.ReplaceAsync(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BusinessCardFront), ct))
                {
                    StatusMessage = "تم فتح قالب بطاقة الأعمال";
                }
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("quickstart.stickerSheet"),
            "بدء سريع: شيت ملصقات",
            "Alt+3",
            "بداية سريعة",
            () => true,
            async ct =>
            {
                if (await _projects.ReplaceAsync(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.StickerSheetA5), ct))
                {
                    StatusMessage = "تم فتح شيت الملصقات البارامتري";
                }
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("quickstart.blankA4"),
            "بدء سريع: ورقة حرة A4",
            "Alt+4",
            "بداية سريعة",
            () => true,
            async ct =>
            {
                if (await _projects.ReplaceAsync(QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BlankFreeformA4), ct))
                {
                    StatusMessage = "تم فتح ورقة حرة A4";
                }
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("collage.transferFreeform"),
            "نقل التصميم الحر إلى الكولاج",
            string.Empty,
            "كولاج",
            () => ActivePage.CollageLayout is null,
            async ct =>
            {
                ZiwaDocument targetCollage = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6);
                ZiwaDocument populated = _transferService.TransferFreeformPageToCollage(ActivePage, targetCollage);
                if (await _projects.ReplaceAsync(populated, ct))
                {
                    StatusMessage = "تم نقل التصميم الحر إلى شيت الكولاج بدقة 300 DPI";
                }
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("collage.insertIntoSelectedSlot"),
            "إدراج صورة في الخانة المحددة",
            string.Empty,
            "كولاج",
            () => IsCollageSlotSelected,
            ImportImageCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("collage.clearAllSlots"),
            "تفريغ كل خانات الكولاج",
            string.Empty,
            "كولاج",
            () => IsCollageMode,
            _ =>
            {
                ClearAllCollageSlots();
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("brand.insertLogo"),
            "إدراج شعار الاستوديو (هويتي)",
            string.Empty,
            "أصولي",
            () => true,
            _ =>
            {
                _session.ApplyCommand("إدراج شعار الهوية", doc => QuickStartAndBrandService.InsertBrandLogo(doc, _brandKit));
                StatusMessage = "تم إدراج شعار الاستوديو";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("brand.insertPhrase"),
            "إدراج عبارة الاستوديو (هويتي)",
            string.Empty,
            "أصولي",
            () => true,
            _ =>
            {
                _session.ApplyCommand("إدراج عبارة الهوية", doc => QuickStartAndBrandService.InsertBrandPhrase(doc, _brandKit, 0));
                StatusMessage = "تم إدراج عبارة الاستوديو";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("app.commandPalette"),
            "لوحة الأوامر السريعة",
            "Ctrl+K",
            "تنقل",
            () => true,
            _ =>
            {
                ToggleCommandPalette();
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.toggleZen"),
            "وضع التركيز الكامل (Zen Mode)",
            "Ctrl+.",
            "عرض",
            () => true,
            _ =>
            {
                IsZenMode = !IsZenMode;
                StatusMessage = IsZenMode ? "تم تفعيل وضع التركيز (Ctrl+. للخروج)" : "تمت استعادة مساحة العمل";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.toggleInspector"),
            "إظهار / إخفاء لوح المفتش",
            "Ctrl+I",
            "عرض",
            () => true,
            _ =>
            {
                _autoOpenedInspector = false;
                IsInspectorOpen = !IsInspectorOpen;
                StatusMessage = IsInspectorOpen ? "تم فتح لوح المفتش" : "تم طي لوح المفتش";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.zoomIn"),
            "تكبير",
            string.Empty,
            "عرض",
            () => true,
            _ =>
            {
                Viewport = Viewport with { Zoom = Math.Min(ViewportTransform.MaxZoom, Viewport.Zoom * 1.25) };
                StatusMessage = $"مستوى التكبير: {ZoomPercentText}";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.zoomOut"),
            "تصغير",
            string.Empty,
            "عرض",
            () => true,
            _ =>
            {
                Viewport = Viewport with { Zoom = Math.Max(ViewportTransform.MinZoom, Viewport.Zoom / 1.25) };
                StatusMessage = $"مستوى التكبير: {ZoomPercentText}";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.resetZoom"),
            "إعادة التكبير 100%",
            "Ctrl+1",
            "عرض",
            () => true,
            _ =>
            {
                Viewport = new ViewportTransform(1.0, 32.0, 32.0, Viewport.DpiScale);
                StatusMessage = "تمت إعادة تعيين المعاينة إلى 100%";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.fitPage"),
            "ملاءمة الصفحة",
            "Ctrl+0",
            "عرض",
            () => true,
            _ =>
            {
                ViewportFitRequested?.Invoke();
                StatusMessage = $"ملاءمة الصفحة ({ZoomPercentText})";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.duplicate"),
            "تكرار التحديد",
            "Ctrl+D",
            "تحرير",
            () => HasSelection,
            _ =>
            {
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                ImmutableArray<NodeId> created = [];
                if (_session.ApplyCommand("تكرار التحديد", doc => FreeformSceneOperations.DuplicateSelection(doc, ids, out created)))
                {
                    _session.SetSelection(created);
                    StatusMessage = $"تم تكرار {created.Length} عنصر";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("edit.selectAll"),
            "تحديد الكل",
            "Ctrl+A",
            "تحرير",
            () => ActivePage.Nodes.Any(n => n.IsVisible && !n.IsLocked),
            _ =>
            {
                ImmutableArray<NodeId> allIds = ActivePage.Nodes
                    .Where(n => n.IsVisible && !n.IsLocked)
                    .Select(n => n.Id)
                    .ToImmutableArray();
                _session.SetSelection(allIds);
                StatusMessage = $"تم تحديد {allIds.Length} عنصر";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("arrange.bringForward"),
            "إحضار للأمام",
            "Ctrl+]",
            "ترتيب",
            () => HasSelection,
            _ =>
            {
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                if (_session.ApplyCommand("إحضار للأمام", doc => FreeformSceneOperations.BringForward(doc, ids)))
                {
                    StatusMessage = "تم تقديم التحديد للأمام";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("arrange.sendBackward"),
            "إرسال للخلف",
            "Ctrl+[",
            "ترتيب",
            () => HasSelection,
            _ =>
            {
                ImmutableArray<NodeId> ids = _session.SelectedNodeIds;
                if (_session.ApplyCommand("إرسال للخلف", doc => FreeformSceneOperations.SendBackward(doc, ids)))
                {
                    StatusMessage = "تم إرسال التحديد للخلف";
                }

                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("view.toggleTheme"),
            "تبديل المظهر",
            string.Empty,
            "عرض",
            () => true,
            _ =>
            {
                IsDarkTheme = !IsDarkTheme;
                StatusMessage = IsDarkTheme ? "تم التبديل إلى المظهر الداكن" : "تم التبديل إلى المظهر الفاتح";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("file.export300Dpi"),
            "تصدير 300 DPI",
            "Ctrl+E",
            "ملف",
            () => true,
            async ct =>
            {
                string? outputPath = await ResolveExportDestinationAsync(
                    "ziwa-vertical-slice-300dpi.png",
                    Document.Title,
                    "png",
                    "صورة PNG",
                    ct);
                if (outputPath is null)
                {
                    return;
                }

                ExportArtifactResult result = await _exportService.ExportActivePageAsync(
                    outputPath,
                    Document.PrintSettings.ExportDpi,
                    asJpeg: false,
                    ct);

                StatusMessage = $"تم التصدير ذرياً: {result.PixelWidth}×{result.PixelHeight} px @ {result.Dpi:0} DPI";
                ShowToast("التصدير السريع", StatusMessage, global::Avalonia.Controls.Notifications.NotificationType.Success);
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("page.add"),
            "إضافة صفحة",
            "Ctrl+Shift+N",
            "صفحات",
            () => true,
            _ =>
            {
                _session.ApplyCommand("إضافة صفحة جديدة", doc => MultiPageOperations.AddBlankPage(doc));
                StatusMessage = $"تمت إضافة {ActivePage.Name}";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("page.duplicate"),
            "تكرار الصفحة",
            string.Empty,
            "صفحات",
            () => true,
            _ =>
            {
                DuplicatePage(ActivePage.Id);
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("page.frontBack"),
            "وجه / ظهر",
            string.Empty,
            "صفحات",
            () => true,
            _ =>
            {
                bool wasSingle = Document.Pages.Length == 1;
                _session.ApplyCommand(
                    wasSingle ? "إنشاء الوجه الخلفي" : "التبديل بين الوجه والظهر",
                    MultiPageOperations.EnsureFrontAndBackPages);
                StatusMessage = wasSingle ? "تم إنشاء الوجهين الأمامي والخلفي" : $"الانتقال إلى {ActivePage.Name}";
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("print.openDialog"),
            "طباعة وتجهيز مطبعي",
            "Ctrl+P",
            "طباعة",
            () => true,
            _ =>
            {
                OpenPrintDialog();
                return Task.CompletedTask;
            }));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("print.direct"),
            "طباعة مباشرة الآن",
            string.Empty,
            "طباعة",
            () => true,
            ct => ExecuteDirectPrintCoreAsync(ct)));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("file.exportPdf"),
            "تصدير Vector PDF",
            string.Empty,
            "ملف",
            () => true,
            ct => ExportVectorPdfCoreAsync(ct)));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("file.exportCmykTiff"),
            "تصدير CMYK TIFF",
            string.Empty,
            "ملف",
            () => true,
            ct => ExportCmykTiffCoreAsync(ct)));

        RegisterRasterExportCommands();
    }

    private void OnSessionChanged()
    {
        if (_session.EditingStickerNodeId is { } editStickerId &&
            ActivePage.Nodes.FirstOrDefault(n => n.Id == editStickerId) is StickerNode sticker)
        {
            OpenStickerStudioForExisting(sticker);
        }

        UpdateContextualInspectorLifecycle();
        RefreshFromSession();
    }

    private void UpdateContextualInspectorLifecycle()
    {
        if (HasAnySelection)
        {
            if (!IsInspectorOpen)
            {
                InspectorTabIndex = PropertiesTabIndex;
                _autoOpenedInspector = true;
                IsInspectorOpen = true;
            }
        }
        else if (_autoOpenedInspector)
        {
            _autoOpenedInspector = false;
            IsInspectorOpen = false;
        }
    }

    private void RefreshFromSession()
    {
        NodeSummaries.Clear();
        foreach (SceneNode n in ActivePage.Nodes)
        {
            NodeSummaries.Add($"{n.Name} ({n.BoundsMm.Width:0.#}×{n.BoundsMm.Height:0.#} mm)");
        }

        if (ActivePage.CollageLayout is { Slots.IsDefaultOrEmpty: false } layout)
        {
            foreach (CollageSlot slot in layout.Slots)
            {
                string state = slot.AssignedAssetId is null ? "فارغة" : "ممتلئة";
                NodeSummaries.Add($"خانة #{slot.Index + 1} — {state} ({slot.BoundsMm.Width:0.#}×{slot.BoundsMm.Height:0.#} mm)");
            }
        }

        if (SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode textNode)
        {
            SelectedTextContent = textNode.Text;
        }
        else if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode imageNode)
        {
            SelectedImageMask = imageNode.Mask;
            SyncFilterFields(imageNode.EffectiveFilters);
        }
        else if (IsCollageSlotSelected && SelectedCollageSlot is { } slot)
        {
            SelectedImageMask = slot.Mask;
            SyncFilterFields(slot.EffectiveFilters);
        }

        for (int i = 0; i < Document.Pages.Length; i++)
        {
            PageModel page = Document.Pages[i];
            if (i == PageTabs.Count || PageTabs[i].Id != page.Id)
            {
                // Keep the tab (and its commands) associated with the page through reorders and renames.
                PageTabItemViewModel? existing = PageTabs.Skip(i).FirstOrDefault(tab => tab.Id == page.Id);
                if (existing is null)
                {
                    PageTabs.Insert(i, new PageTabItemViewModel(this, page.Id));
                }
                else
                {
                    PageTabs.Move(PageTabs.IndexOf(existing), i);
                }
            }

            PageTabItemViewModel tab = PageTabs[i];
            tab.Index = i;
            tab.Name = page.Name;
            tab.IsActive = i == Document.ActivePageIndex;
            tab.PageCount = Document.Pages.Length;
        }

        while (PageTabs.Count > Document.Pages.Length)
        {
            PageTabs.RemoveAt(PageTabs.Count - 1);
        }

        PreflightIssues.Clear();
        Func<AssetId, AssetPixelDimensions?>? resolver = _assetRegistrar is null
            ? null
            : _assetRegistrar.TryGetAssetDimensions;
        foreach (PreflightIssue issue in PreflightChecker.CheckDocument(Document, resolver))
        {
            PreflightIssues.Add(issue);
        }

        if (IsPrintDialogOpen)
        {
            RecomputePrintCalibration();
        }

        EditableDocumentTitle = Document.Title;
        OnPropertyChanged(nameof(ActivePage));
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(ProjectWindowTitle));
        OnPropertyChanged(nameof(PageName));
        OnPropertyChanged(nameof(PageWidthMm));
        OnPropertyChanged(nameof(PageHeightMm));
        OnPropertyChanged(nameof(PageSizeMmText));
        OnPropertyChanged(nameof(ActivePageCounterText));
        OnPropertyChanged(nameof(ActivePageDisplayText));
        OnPropertyChanged(nameof(IsMultiPage));
        OnPropertyChanged(nameof(CanDeleteActivePage));
        OnPropertyChanged(nameof(CanMovePageEarlier));
        OnPropertyChanged(nameof(CanMovePageLater));
        OnPropertyChanged(nameof(HasPreflightWarnings));
        OnPropertyChanged(nameof(PreflightSummaryText));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsCollageMode));
        OnPropertyChanged(nameof(IsCollageSlotSelected));
        OnPropertyChanged(nameof(IsCollageSlotWithImageSelected));
        OnPropertyChanged(nameof(HasAnySelection));
        OnPropertyChanged(nameof(CanGroup));
        OnPropertyChanged(nameof(CanUngroup));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(IsImageSelected));
        OnPropertyChanged(nameof(IsSvgSelected));
        OnPropertyChanged(nameof(IsStickerSelected));
        OnPropertyChanged(nameof(IsPhotoToolsVisible));
        OnPropertyChanged(nameof(IsPhotoAiTargetVisible));
        OnPropertyChanged(nameof(CanRestoreOriginalTarget));
        OnPropertyChanged(nameof(IsQuickBarVisible));
        OnPropertyChanged(nameof(CanMaskImageWithShape));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(WorkspaceModeBadgeText));
        SyncInspectorPolishFields();
        SyncCanvasInteractionFields();
        SyncPrintExportOptions();
    }

    private void SyncFilterFields(ImageFilterSpec filters)
    {
        FilterBrightness = filters.Brightness;
        FilterContrast = filters.Contrast;
        FilterSaturation = filters.Saturation;
        SelectedFilterPreset = filters.Preset;
    }

    private ImageFilterSpec BuildCurrentFilterSpec() =>
        new(
            Brightness: FilterBrightness,
            Contrast: FilterContrast,
            Saturation: FilterSaturation,
            Preset: SelectedFilterPreset);

    private void LoadStickerStudioFromTemplate(
        StickerTemplateDefinition template,
        IReadOnlyDictionary<string, string>? existingFields,
        StickerPalette? palette)
    {
        bool prevSync = _isSyncingStickerStudio;
        _isSyncingStickerStudio = true;
        try
        {
            SelectedStickerTemplate = template;
            CurrentStickerPalette = palette ?? template.DefaultPalette;
            StickerCustomPrimaryColor = ToAvaloniaColor(CurrentStickerPalette.Primary);

            foreach (StickerFieldViewModel item in StickerFieldItems)
            {
                item.PropertyChanged -= OnStickerFieldChanged;
            }

            StickerFieldItems.Clear();
            foreach (StickerFieldSpec spec in template.Fields)
            {
                string val = existingFields is not null && existingFields.TryGetValue(spec.Key, out string? existing)
                    ? existing
                    : spec.DefaultValue;
                var fieldVm = new StickerFieldViewModel(spec.Key, spec.LabelAr, val);
                fieldVm.PropertyChanged += OnStickerFieldChanged;
                StickerFieldItems.Add(fieldVm);
            }
        }
        finally
        {
            _isSyncingStickerStudio = prevSync;
        }

        RefreshStickerPreview();
    }

    private void OnStickerFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isSyncingStickerStudio && e.PropertyName == nameof(StickerFieldViewModel.Value))
        {
            RefreshStickerPreview();
        }
    }

    private void OpenNewStickerStudio()
    {
        _editingStickerTargetId = null;
        LoadStickerStudioFromTemplate(ParametricStickerCatalog.CommercialSeal, null, null);
        IsStickerStudioOpen = true;
    }

    private void OpenStickerStudioForExisting(StickerNode sticker)
    {
        _editingStickerTargetId = sticker.Id;
        StickerTemplateDefinition template = ParametricStickerCatalog.ResolveTemplate(sticker.TemplateId);
        LoadStickerStudioFromTemplate(template, sticker.Fields, sticker.Palette);
        IsStickerStudioOpen = true;
    }

    private void RefreshFilteredCommands()
    {
        FilteredCommands.Clear();
        string q = CommandSearchQuery.Trim();
        foreach (CommandDefinition cmd in _commandRegistry.GetAll())
        {
            if (string.IsNullOrEmpty(q) ||
                cmd.TitleAr.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.ShortcutGesture.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCommands.Add(cmd);
            }
        }

        OnPropertyChanged(nameof(HasNoFilteredCommands));
    }

    partial void OnViewportChanged(ViewportTransform value) => OnPropertyChanged(nameof(ZoomPercentText));

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (global::Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        OnPropertyChanged(nameof(ThemeToggleText));
    }

    partial void OnIsInspectorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveInspectorVisible));
        OnPropertyChanged(nameof(CanShowReopenInspectorButton));
    }

    partial void OnIsZenModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChromeVisible));
        OnPropertyChanged(nameof(IsQuickBarVisible));
        OnPropertyChanged(nameof(EffectiveInspectorVisible));
        OnPropertyChanged(nameof(CanShowReopenInspectorButton));
    }

    partial void OnCommandSearchQueryChanged(string value) => RefreshFilteredCommands();

    partial void OnSelectedStickerTemplateChanged(StickerTemplateDefinition value)
    {
        if (!_isSyncingStickerStudio)
        {
            LoadStickerStudioFromTemplate(value, null, null);
        }
    }

    partial void OnCurrentStickerPaletteChanged(StickerPalette value)
    {
        OnPropertyChanged(nameof(StudioPrimaryBrush));
        OnPropertyChanged(nameof(StudioSecondaryBrush));
        OnPropertyChanged(nameof(StudioAccentBrush));
        OnPropertyChanged(nameof(StudioSurfaceBrush));
        if (!_isSyncingStickerStudio)
        {
            _isSyncingStickerStudio = true;
            try
            {
                StickerCustomPrimaryColor = ToAvaloniaColor(value.Primary);
            }
            finally
            {
                _isSyncingStickerStudio = false;
            }

            RefreshStickerPreview();
        }
    }

    partial void OnStickerCustomPrimaryColorChanged(Color value)
    {
        if (_isSyncingStickerStudio)
        {
            return;
        }

        _isSyncingStickerStudio = true;
        try
        {
            CurrentStickerPalette = OklchPaletteGenerator.GeneratePaletteFromPrimaryColor(ToColorRgba(value));
        }
        finally
        {
            _isSyncingStickerStudio = false;
        }

        RefreshStickerPreview();
    }

    private void RefreshStickerPreview()
    {
        StickerTemplateDefinition template = SelectedStickerTemplate;
        ImmutableDictionary<string, string> fields = StickerFieldItems
            .ToImmutableDictionary(f => f.Key, f => f.Value);
        var previewNode = new StickerNode(
            Id: NodeId.New(),
            Name: template.TitleAr,
            BoundsMm: new RectMm(0.0, 0.0, template.DefaultWidthMm, template.DefaultHeightMm),
            TemplateId: template.Id,
            Fields: fields,
            Palette: CurrentStickerPalette);

        var previewPage = new PageModel(
            PageId.New(),
            "معاينة الملصق",
            template.DefaultWidthMm,
            template.DefaultHeightMm,
            0.0,
            0.0,
            new TransparentBackground(),
            [previewNode]);

        const int bmpW = 320;
        const int bmpH = 180;
        var nextBmp = new SKBitmap(bmpW, bmpH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(nextBmp))
        {
            canvas.Clear(SKColors.Transparent);
            double pad = 8.0;
            double scale = Math.Min(
                (bmpW - (pad * 2.0)) / Math.Max(1.0, template.DefaultWidthMm),
                (bmpH - (pad * 2.0)) / Math.Max(1.0, template.DefaultHeightMm));
            float ox = (float)((bmpW - (template.DefaultWidthMm * scale)) * 0.5);
            float oy = (float)((bmpH - (template.DefaultHeightMm * scale)) * 0.5);
            canvas.Translate(ox, oy);
            canvas.Scale((float)scale, (float)scale);
            SceneRenderer.RenderPage(
                canvas,
                previewPage,
                new global::Ziwa.Rendering.RenderOptions(
                    Dpi: 96.0,
                    QualityMode: RenderQualityMode.InteractivePreview,
                    IsPreScaledToMillimeters: true));
        }

        SKBitmap? oldBmp = _stickerPreviewBitmap;
        _stickerPreviewBitmap = nextBmp;
        OnPropertyChanged(nameof(StickerPreviewBitmap));
        if (oldBmp is not null)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => oldBmp.Dispose(), DispatcherPriority.Background);
            }
            else
            {
                oldBmp.Dispose();
            }
        }
    }

    partial void OnSelectedImageMaskChanged(MaskShapeKind value)
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode img && img.Mask != value)
        {
            _session.ApplyCommand("تغيير قناع الصورة", doc => FreeformSceneOperations.SetImageMask(doc, img.Id, value));
        }
        else if (IsCollageSlotSelected &&
                 _session.SelectedCollageSlotId is { } sid &&
                 SelectedCollageSlot is { } slot &&
                 slot.Mask != value)
        {
            _session.ApplyCommand("تغيير قناع الخانة", doc => CollageOperations.SetSlotMask(doc, sid, value));
        }
    }

    partial void OnSelectedPrinterChanged(PrinterDeviceInfo? value) => RecomputePrintCalibration();

    partial void OnSelectedPrintOrientationChanged(PrintOrientationKind value) => RecomputePrintCalibration();

    partial void OnPrintBorderlessChanged(bool value) => RecomputePrintCalibration();

    partial void OnPrintIncludeBleedChanged(bool value) => RecomputePrintCalibration();

    partial void OnPrintIncludeCropMarksChanged(bool value) => RecomputePrintCalibration();

    partial void OnPrintCopiesChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 99);
        if (clamped != value)
        {
            PrintCopies = clamped;
        }
    }

    partial void OnLastSpoolJobIdChanged(int? value) => OnPropertyChanged(nameof(CanCancelLastPrintJob));

    partial void OnLastSpoolPrinterNameChanged(string? value) => OnPropertyChanged(nameof(CanCancelLastPrintJob));

    [RelayCommand]
    private void CloseInspector()
    {
        _autoOpenedInspector = false;
        IsInspectorOpen = false;
    }

    [RelayCommand]
    private void OpenInspector()
    {
        _autoOpenedInspector = false;
        IsInspectorOpen = true;
    }

    [RelayCommand]
    private void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;
        if (IsCommandPaletteOpen)
        {
            CommandSearchQuery = string.Empty;
            RefreshFilteredCommands();
        }
    }

    [RelayCommand]
    private async Task ExecutePaletteCommandAsync(CommandDefinition? cmd, CancellationToken ct)
    {
        if (cmd is not null && await _commandRegistry.TryExecuteAsync(cmd.Id, ct))
        {
            IsCommandPaletteOpen = false;
        }
    }

    [RelayCommand]
    private void EditSelectedSticker()
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode sticker)
        {
            _session.OpenStickerEditor(sticker.Id);
        }
    }

    [RelayCommand]
    private void RandomizeStickerPalette()
    {
        _paletteSeed += 37;
        CurrentStickerPalette = OklchPaletteGenerator.GenerateSmartRandomPalette(_paletteSeed);
    }

    [RelayCommand]
    private void RandomizeSelectedStickerPalette()
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is StickerNode sticker)
        {
            _paletteSeed += 41;
            StickerPalette nextPalette = OklchPaletteGenerator.GenerateSmartRandomPalette(_paletteSeed);
            _session.ApplyCommand("توليفة ألوان OKLCH ذكية", doc =>
                StickerOperations.UpdateStickerInPlace(doc, sticker.Id, sticker.TemplateId, sticker.Fields, nextPalette));
        }
    }

    [RelayCommand]
    private void ApplyStickerStudio()
    {
        ImmutableDictionary<string, string> fields = StickerFieldItems
            .ToImmutableDictionary(f => f.Key, f => f.Value);

        if (_editingStickerTargetId is { } existingId)
        {
            _session.ApplyCommand("تعديل الملصق في مكانه", doc =>
                StickerOperations.UpdateStickerInPlace(doc, existingId, SelectedStickerTemplate.Id, fields, CurrentStickerPalette));
        }
        else
        {
            StickerNode created = StickerOperations.CreateStickerFromTemplate(
                SelectedStickerTemplate.Id,
                ActivePage.WidthMm * 0.5,
                ActivePage.HeightMm * 0.5) with
            {
                Fields = fields,
                Palette = CurrentStickerPalette,
            };
            _session.ApplyCommand("إدراج ملصق بارامتري", doc => FreeformSceneOperations.AddNode(doc, created));
            _session.SetSelection([created.Id]);
        }

        _editingStickerTargetId = null;
        _session.OpenStickerEditor(null);
        IsStickerStudioOpen = false;
        StatusMessage = "تم حفظ الملصق البارامتري";
    }

    [RelayCommand]
    private void CloseStickerStudio()
    {
        _editingStickerTargetId = null;
        _session.OpenStickerEditor(null);
        IsStickerStudioOpen = false;
    }

    [RelayCommand]
    private void ApplyImageFilters()
    {
        ImageFilterSpec spec = BuildCurrentFilterSpec();
        if (IsCollageSlotSelected && _session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("تطبيق مرشح الخانة", doc => CollageOperations.ApplyFiltersToSlot(doc, sid, spec));
            StatusMessage = "تم تطبيق المرشح على الخانة";
        }
        else if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode img)
        {
            _session.ApplyCommand("تطبيق مرشح الصورة", doc => FreeformSceneOperations.ApplyImageFilters(doc, img.Id, spec));
            StatusMessage = "تم تطبيق المرشح على الصورة";
        }
    }

    [RelayCommand]
    private void ApplyFiltersToAllSlots()
    {
        if (!IsCollageMode)
        {
            return;
        }

        ImageFilterSpec spec = BuildCurrentFilterSpec();
        _session.ApplyCommand("تطبيق المرشح على كل الخانات", doc => CollageOperations.ApplyFiltersToAllSlots(doc, spec));
        StatusMessage = "تم تعميم المرشح على جميع الخانات";
    }

    [RelayCommand]
    private void ResetImageOrSlotFilters()
    {
        SyncFilterFields(ImageFilterSpec.None);
        if (IsCollageSlotSelected && _session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("إعادة تعيين مرشح الخانة", doc => CollageOperations.ResetSlotFilters(doc, sid));
        }
        else if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode img)
        {
            _session.ApplyCommand("إعادة تعيين مرشح الصورة", doc => FreeformSceneOperations.ResetImageFilters(doc, img.Id));
        }
    }

    [RelayCommand]
    private void RotateSelectedSlot()
    {
        if (_session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("تدوير محتوى الخانة 90°", doc => CollageOperations.TransformSlotContent(doc, sid, quarterTurnDelta: 1));
        }
    }

    [RelayCommand]
    private void FlipSelectedSlot()
    {
        if (_session.SelectedCollageSlotId is { } sid)
        {
            _session.ApplyCommand("قلب محتوى الخانة أفقياً", doc => CollageOperations.TransformSlotContent(doc, sid, toggleFlipH: true));
        }
    }

    [RelayCommand]
    private void ToggleAutoRepeat()
    {
        if (ActivePage.CollageLayout is { } layout)
        {
            _session.ApplyCommand("تكرار صورة الهوية على الشيت", doc => CollageOperations.SetAutoRepeatMode(doc, !layout.AutoRepeatMode));
        }
    }

    [RelayCommand]
    private Task UndoAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("edit.undo"), ct);

    [RelayCommand]
    private Task RedoAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("edit.redo"), ct);

    [RelayCommand]
    private Task DeleteSelectionAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("edit.delete"), ct);

    [RelayCommand]
    private Task GroupAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("arrange.group"), ct);

    [RelayCommand]
    private Task UngroupAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("arrange.ungroup"), ct);

    [RelayCommand]
    private Task MaskWithShapeAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("image.maskWithShape"), ct);

    [RelayCommand]
    private Task RestoreOriginalImageAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("image.restoreOriginal"), ct);

    [RelayCommand]
    private Task AddHeadingTextAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.headingText"), ct);

    [RelayCommand]
    private Task AddSubheadingTextAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.subheadingText"), ct);

    [RelayCommand]
    private Task AddCapsuleBadgeAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.capsuleBadge"), ct);

    [RelayCommand]
    private Task AddCurvedSealAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.curvedSeal"), ct);

    [RelayCommand]
    private Task AddWatermarkTextAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.watermark"), ct);

    [RelayCommand]
    private Task AddStarShapeAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("insert.starShape"), ct);

    [RelayCommand]
    private Task OpenStickerStudioAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("sticker.openStudio"), ct);

    [RelayCommand]
    private Task ToggleSvgTintAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("svg.toggleTint"), ct);

    [RelayCommand]
    private Task StartIdPhotoSheetAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("quickstart.idPhotoSheet"), ct);

    [RelayCommand]
    private Task StartBusinessCardAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("quickstart.businessCard"), ct);

    [RelayCommand]
    private Task StartStickerSheetAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("quickstart.stickerSheet"), ct);

    [RelayCommand]
    private Task StartFreeformSheetAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("quickstart.blankA4"), ct);

    [RelayCommand]
    private Task TransferFreeformToCollageAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("collage.transferFreeform"), ct);

    [RelayCommand]
    private Task InsertBrandLogoAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("brand.insertLogo"), ct);

    [RelayCommand]
    private Task InsertBrandPhraseAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("brand.insertPhrase"), ct);

    [RelayCommand]
    private Task ToggleZenModeAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.toggleZen"), ct);

    [RelayCommand]
    private void CommitSelectedText()
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode txt)
        {
            _session.ApplyCommand("تعديل النص", doc => FreeformSceneOperations.UpdateTextNodeContent(doc, txt.Id, SelectedTextContent));
        }
    }

    [RelayCommand]
    private Task ToggleThemeAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.toggleTheme"), ct);

    [RelayCommand]
    private Task ZoomInAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.zoomIn"), ct);

    [RelayCommand]
    private Task ZoomOutAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.zoomOut"), ct);

    [RelayCommand]
    private Task ResetZoomAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.resetZoom"), ct);

    [RelayCommand]
    private Task ExportSample300DpiAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.export300Dpi"), ct);

    [RelayCommand]
    private Task AddPageAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("page.add"), ct);

    [RelayCommand]
    private Task DuplicateActivePageAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("page.duplicate"), ct);

    [RelayCommand]
    private Task EnsureOrToggleFrontBackAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("page.frontBack"), ct);

    [RelayCommand]
    private void SelectPageByIndex(int index)
    {
        if (index < 0 || index >= Document.Pages.Length)
        {
            return;
        }

        _session.ApplyCommand("الانتقال بين الصفحات", doc => MultiPageOperations.SelectPage(doc, index));
        StatusMessage = $"تم تحديد {ActivePage.Name}";
    }

    [RelayCommand]
    private void SelectPreviousPage()
    {
        if (Document.ActivePageIndex > 0)
        {
            SelectPageByIndex(Document.ActivePageIndex - 1);
        }
    }

    [RelayCommand]
    private void SelectNextPage()
    {
        if (Document.ActivePageIndex < Document.Pages.Length - 1)
        {
            SelectPageByIndex(Document.ActivePageIndex + 1);
        }
    }

    [RelayCommand]
    private void MovePageEarlier() => MovePage(ActivePage.Id, -1);

    [RelayCommand]
    private void MovePageLater() => MovePage(ActivePage.Id, 1);

    [RelayCommand]
    private void DeleteActivePage() => DeletePage(ActivePage.Id);

    internal void DuplicatePage(PageId id)
    {
        if (ApplyPageCommand(id, "تكرار الصفحة", MultiPageOperations.DuplicatePage) is not null)
        {
            StatusMessage = $"تم تكرار الصفحة إلى {ActivePage.Name}";
        }
    }

    internal void MovePage(PageId id, int direction)
    {
        string description = direction < 0 ? "تقديم الصفحة" : "تأخير الصفحة";
        if (ApplyPageCommand(id, description, (doc, i) => MultiPageOperations.MovePage(doc, i, i + direction)) is { } moved)
        {
            StatusMessage = direction < 0 ? $"تم تقديم {moved.Name}" : $"تم تأخير {moved.Name}";
        }
    }

    internal void DeletePage(PageId id)
    {
        if (Document.Pages.Length <= 1)
        {
            StatusMessage = "لا يمكن حذف الصفحة الوحيدة في المستند";
            return;
        }

        if (ApplyPageCommand(id, "حذف الصفحة", MultiPageOperations.DeletePage) is not null)
        {
            StatusMessage = $"تم حذف الصفحة — النشطة الآن: {ActivePage.Name}";
        }
    }

    private PageModel? ApplyPageCommand(PageId id, string description, Func<ZiwaDocument, int, ZiwaDocument> edit)
    {
        PageModel? target = null;
        bool applied = _session.ApplyCommand(description, doc =>
        {
            for (int i = 0; i < doc.Pages.Length; i++)
            {
                if (doc.Pages[i].Id == id)
                {
                    target = doc.Pages[i];
                    return edit(doc, i);
                }
            }

            return doc;
        });
        return applied ? target : null;
    }

    [RelayCommand]
    private void SelectPreflightIssue(PreflightIssue? issue)
    {
        if (issue is null)
        {
            return;
        }

        if (issue.PageIndex >= 0 &&
            issue.PageIndex < Document.Pages.Length &&
            issue.PageIndex != Document.ActivePageIndex)
        {
            _session.ApplyCommand("الانتقال لصفحة التنبيه", doc => MultiPageOperations.SelectPage(doc, issue.PageIndex));
        }

        if (issue.NodeId is { } nodeId)
        {
            _session.SetSelection([nodeId]);
        }
        else if (issue.SlotId is { } slotId)
        {
            _session.SelectCollageSlot(slotId);
        }

        StatusMessage = issue.MessageAr;
    }

    [RelayCommand]
    private void OpenPrintDialog()
    {
        RefreshPrinters();
        PrintAllPages = Document.Pages.Length > 1;
        RecomputePrintCalibration();
        IsPrintDialogOpen = true;
    }

    [RelayCommand]
    private void ClosePrintDialog()
    {
        IsPrintDialogOpen = false;
    }

    [RelayCommand]
    private void SelectVirtualPreviewPrinter()
    {
        if (AvailablePrinters.Count == 0)
        {
            RefreshPrinters();
        }

        SelectedPrinter = AvailablePrinters.FirstOrDefault(p => p.IsVirtualPreview)
            ?? AvailablePrinters.FirstOrDefault();
        RecomputePrintCalibration();
    }

    [RelayCommand]
    private Task ExecuteDirectPrintAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("print.direct"), ct);

    [RelayCommand]
    private void CancelLastPrintJob()
    {
        if (_printSpooler is null || LastSpoolJobId is not { } jobId || string.IsNullOrWhiteSpace(LastSpoolPrinterName))
        {
            return;
        }

        bool cancelled = _printSpooler.CancelSpoolJob(LastSpoolPrinterName, jobId);
        StatusMessage = cancelled
            ? $"تم إلغاء مهمة الطباعة #{jobId}"
            : $"تعذر إلغاء مهمة الطباعة #{jobId}";

        if (cancelled)
        {
            LastSpoolJobId = null;
        }
    }

    [RelayCommand]
    private Task ExportVectorPdfAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.exportPdf"), ct);

    [RelayCommand]
    private Task ExportCmykTiffAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("file.exportCmykTiff"), ct);

    private void RefreshPrinters()
    {
        AvailablePrinters.Clear();

        IReadOnlyList<PrinterDeviceInfo> printers = _printSpooler is not null
            ? _printSpooler.EnumeratePrinters()
            :
            [
                new PrinterDeviceInfo(
                    PrintCalibrationPlanner.VirtualPrinterName,
                    IsDefault: true,
                    IsVirtualPreview: true,
                    StatusTextAr: PrintCalibrationPlanner.VirtualPrinterReadyStatusAr),
            ];

        foreach (PrinterDeviceInfo printer in printers)
        {
            AvailablePrinters.Add(printer);
        }

        SelectedPrinter = AvailablePrinters.FirstOrDefault(p => p.IsDefault)
            ?? AvailablePrinters.FirstOrDefault();
    }

    private void RecomputePrintCalibration()
    {
        string printerName = SelectedPrinter?.PrinterName ?? PrintCalibrationPlanner.VirtualPrinterName;
        HardwarePrintCapabilities capabilities;

        try
        {
            capabilities = _printSpooler is not null
                ? _printSpooler.QueryCapabilities(printerName, SelectedPrintOrientation)
                : PrintCalibrationPlanner.CreateVirtualA4Capabilities(printerName, SelectedPrintOrientation, PrintBorderless);
        }
        catch (Exception ex)
        {
            capabilities = PrintCalibrationPlanner.CreateVirtualA4Capabilities(printerName, SelectedPrintOrientation, PrintBorderless);
            StatusMessage = $"تعذر قراءة مقاسات الطابعة الفعلية؛ تم استخدام المعايرة الافتراضية ({ex.Message})";
        }

        var ticket = new DirectPrintJobTicket(
            PrinterName: printerName,
            DocumentTitle: Document.Title,
            Orientation: SelectedPrintOrientation,
            Copies: Math.Clamp(PrintCopies, 1, 99),
            Borderless: PrintBorderless,
            IncludeBleed: PrintIncludeBleed,
            IncludeCropMarks: PrintIncludeCropMarks,
            PageRange: PrintAllPages ? ExportPageRangeKind.AllPages : ExportPageRangeKind.ActivePageOnly);

        PhysicalPrintCalibrationPlan plan = PrintCalibrationPlanner.ComputeCalibrationPlan(ActivePage, capabilities, ticket);
        HasHardwareMarginClipWarning = plan.ClipsAtHardwareMargins;
        PrintCalibrationSummaryText =
            $"الورقة: {plan.SheetWidthMm:0.#}×{plan.SheetHeightMm:0.#} mm · " +
            $"المحتوى 1:1: {plan.ContentWidthMm:0.#}×{plan.ContentHeightMm:0.#} mm " +
            $"عند ({plan.ContentPhysicalLeftMm:0.#}, {plan.ContentPhysicalTopMm:0.#}) mm · " +
            $"{capabilities.DpiX}×{capabilities.DpiY} DPI";
    }

    private async Task ExecuteDirectPrintCoreAsync(CancellationToken ct)
    {
        if (_printSpooler is null)
        {
            StatusMessage = "خدمة الطباعة المباشرة غير مهيأة";
            return;
        }

        string printerName = SelectedPrinter?.PrinterName ?? PrintCalibrationPlanner.VirtualPrinterName;
        var ticket = new DirectPrintJobTicket(
            PrinterName: printerName,
            DocumentTitle: Document.Title,
            Orientation: SelectedPrintOrientation,
            Copies: Math.Clamp(PrintCopies, 1, 99),
            Borderless: PrintBorderless,
            IncludeBleed: PrintIncludeBleed,
            IncludeCropMarks: PrintIncludeCropMarks,
            PageRange: PrintAllPages ? ExportPageRangeKind.AllPages : ExportPageRangeKind.ActivePageOnly);

        PrintJobSubmissionResult result = await _printSpooler.PrintDocumentAsync(Document, ticket, ct);
        LastSpoolPrinterName = result.PrinterName;
        LastSpoolJobId = result.SpoolJobId;
        StatusMessage = $"تم إرسال مهمة الطباعة #{result.SpoolJobId} ({result.PagesPrinted} صفحة) إلى {result.PrinterName}";
        ShowToast("الطباعة المباشرة", StatusMessage, global::Avalonia.Controls.Notifications.NotificationType.Success);
    }

    private async Task ExportVectorPdfCoreAsync(CancellationToken ct)
    {
        string? outputPath = await ResolveExportDestinationAsync(
            "ziwa-export.pdf",
            Document.Title,
            "pdf",
            "مستند PDF متجه",
            ct);
        if (outputPath is null)
        {
            return;
        }

        ExportPageRangeKind range = PrintAllPages ? ExportPageRangeKind.AllPages : ExportPageRangeKind.ActivePageOnly;
        ExportArtifactResult result = await _exportService.ExportVectorPdfAsync(
            outputPath,
            Document.PrintSettings.ExportDpi,
            pageRange: range,
            includeBleed: PrintIncludeBleed,
            includeCropMarks: PrintIncludeCropMarks,
            includeRegistrationMarks: PrintIncludeRegistrationMarks,
            includeCutContour: PrintIncludeCutContour,
            cancellationToken: ct);

        StatusMessage = $"تم تصدير Vector PDF ذرياً ({result.ByteLength} بايت): {result.FilePath}";
        ShowToast("تصدير PDF", StatusMessage, global::Avalonia.Controls.Notifications.NotificationType.Success);
    }

    private async Task ExportCmykTiffCoreAsync(CancellationToken ct)
    {
        string? outputPath = await ResolveExportDestinationAsync(
            "ziwa-export-cmyk.tiff",
            Document.Title,
            "tiff",
            "صورة طباعية CMYK TIFF",
            ct);
        if (outputPath is null)
        {
            return;
        }

        ExportArtifactResult result = await _exportService.ExportActivePageCmykTiffAsync(
            outputPath,
            Document.PrintSettings.ExportDpi,
            profileKind: SelectedCmykProfile,
            includeBleed: PrintIncludeBleed,
            includeCropMarks: PrintIncludeCropMarks,
            includeRegistrationMarks: PrintIncludeRegistrationMarks,
            includeCutContour: PrintIncludeCutContour,
            cancellationToken: ct);

        StatusMessage = $"تم تصدير CMYK TIFF ذرياً ({result.PixelWidth}×{result.PixelHeight} px): {result.FilePath}";
        ShowToast("تصدير CMYK TIFF", StatusMessage, global::Avalonia.Controls.Notifications.NotificationType.Success);
    }
}
