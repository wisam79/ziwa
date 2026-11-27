using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Commands;
using Ziwa.Application.Scanner;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;
using Ziwa.Infrastructure.Scanner;
using Ziwa.Rendering.Ai;

namespace Ziwa.App.ViewModels;

public sealed record DocumentScanFilterOption(DocumentScanFilterKind Filter, string TitleAr);

public sealed partial class MainWindowViewModel
{
    private DocumentScannerCoordinator? _scannerCoordinator;
    private AssetId _scannerSourceAssetId;
    private int _scannerSourceFullWidthPx;
    private int _scannerSourceFullHeightPx;
    private SKBitmap? _scannerSourceBitmap;
    private SKBitmap? _scannerRectifiedPreviewBitmap;

    [ObservableProperty]
    private bool _isDocumentScannerOpen;

    [ObservableProperty]
    private int _selectedScannerRegionIndex;

    [ObservableProperty]
    private DocumentScanFilterOption _selectedScanFilterOption = new(
        DocumentScanFilterKind.MagicColor,
        DocumentScanFilterCatalog.GetTitleAr(DocumentScanFilterKind.MagicColor));

    [ObservableProperty]
    private bool _insertAllScannedRegions = true;

    [ObservableProperty]
    private string _scannerStatusText = "اختر صورة ممسوحة لبدء استعدال المنظور";

    [ObservableProperty]
    private string _scannerPreviewSummaryText = string.Empty;

    public ObservableCollection<DetectedDocumentRegion> ScannerRegions { get; } = [];

    public IReadOnlyList<DocumentScanFilterOption> AvailableScanFilterOptions { get; } =
        DocumentScanFilterCatalog.All
            .Select(f => new DocumentScanFilterOption(f, DocumentScanFilterCatalog.GetTitleAr(f)))
            .ToArray();

    public SKBitmap? ScannerSourceBitmap => _scannerSourceBitmap;
    public SKBitmap? ScannerRectifiedPreviewBitmap => _scannerRectifiedPreviewBitmap;

    public bool IsScannerProcessing => _scannerCoordinator?.IsProcessing == true;
    public bool IsScannerToolsEnabled => !IsScannerProcessing;
    public bool CanRemoveScanRegion => ScannerRegions.Count > 1 && SelectedScannerRegionIndex >= 0 && SelectedScannerRegionIndex < ScannerRegions.Count;

    private void InitializeScannerIntegration(DocumentScannerCoordinator? scannerCoordinator)
    {
        _scannerCoordinator = scannerCoordinator;
        if (_scannerCoordinator is not null)
        {
            _scannerCoordinator.StateChanged += OnScannerCoordinatorStateChanged;
        }

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("scanner.open"),
            "الماسح الذكي للمستندات والبطاقات",
            "Ctrl+Shift+D",
            "ماسح ضوئي",
            () => _scannerCoordinator is not null && _assetStore is not null && IsScannerToolsEnabled,
            OpenDocumentScannerCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("scanner.openFromFile"),
            "فتح صورة في الماسح الضوئي…",
            string.Empty,
            "ماسح ضوئي",
            () => _scannerCoordinator is not null && _imageImporter is not null && _dialogs is not null && IsScannerToolsEnabled,
            LoadScannerImageFromFileAsync));
    }

    partial void OnSelectedScannerRegionIndexChanged(int value)
    {
        if (value >= 0 && value < ScannerRegions.Count)
        {
            DocumentScanFilterKind filter = ScannerRegions[value].Filter;
            DocumentScanFilterOption? match = AvailableScanFilterOptions.FirstOrDefault(o => o.Filter == filter);
            if (match is not null && !Equals(SelectedScanFilterOption, match))
            {
                SelectedScanFilterOption = match;
            }

            RefreshScannerRectifiedPreview();
        }

        OnPropertyChanged(nameof(CanRemoveScanRegion));
    }

    partial void OnSelectedScanFilterOptionChanged(DocumentScanFilterOption value)
    {
        if (value is null || SelectedScannerRegionIndex < 0 || SelectedScannerRegionIndex >= ScannerRegions.Count)
        {
            return;
        }

        DetectedDocumentRegion current = ScannerRegions[SelectedScannerRegionIndex];
        if (current.Filter != value.Filter)
        {
            ScannerRegions[SelectedScannerRegionIndex] = current with { Filter = value.Filter };
            OnPropertyChanged(nameof(ScannerRegions));
            RefreshScannerRectifiedPreview();
        }
    }

    public void UpdateScannerRegionCorner(int regionIndex, int cornerIndex, PointPx newPointPx, bool refreshRectifiedPreview = true)
    {
        if (!IsScannerToolsEnabled ||
            regionIndex < 0 ||
            regionIndex >= ScannerRegions.Count ||
            _scannerSourceFullWidthPx <= 0 ||
            _scannerSourceFullHeightPx <= 0)
        {
            return;
        }

        DetectedDocumentRegion current = ScannerRegions[regionIndex];
        DocumentQuadPx updatedQuad = current.Quad.WithCorner(
            cornerIndex,
            newPointPx,
            _scannerSourceFullWidthPx,
            _scannerSourceFullHeightPx);

        if (!Equals(updatedQuad, current.Quad))
        {
            ScannerRegions[regionIndex] = current with { Quad = updatedQuad };
            OnPropertyChanged(nameof(ScannerRegions));
            if (refreshRectifiedPreview && regionIndex == SelectedScannerRegionIndex)
            {
                RefreshScannerRectifiedPreview();
            }
        }
    }

    public void RefreshScannerRectifiedPreview()
    {
        SKBitmap? oldPreview = _scannerRectifiedPreviewBitmap;
        SKBitmap? nextPreview = null;

        if (_scannerSourceBitmap is { Handle: not 0 } src &&
            SelectedScannerRegionIndex >= 0 &&
            SelectedScannerRegionIndex < ScannerRegions.Count)
        {
            DetectedDocumentRegion region = ScannerRegions[SelectedScannerRegionIndex];
            nextPreview = OpenCvDocumentScanner.RenderLiveRectifiedPreviewBitmap(
                src,
                region.Quad,
                _scannerSourceFullWidthPx,
                _scannerSourceFullHeightPx,
                region.Filter,
                maxPreviewEdgePx: 540);

            (int outW, int outH) = region.Quad.ComputeOutputDimensionsPx();
            ScannerPreviewSummaryText = $"{region.LabelAr} · {outW}×{outH} px · {DocumentScanFilterCatalog.GetTitleAr(region.Filter)}";
        }
        else
        {
            ScannerPreviewSummaryText = string.Empty;
        }

        _scannerRectifiedPreviewBitmap = nextPreview;
        OnPropertyChanged(nameof(ScannerRectifiedPreviewBitmap));
        DisposeBitmapAfterCompositorPass(oldPreview);
    }

    [RelayCommand]
    private Task OpenDocumentScannerAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("scanner.open"), ct);

    [RelayCommand]
    private async Task LoadScannerImageFromFileAsync(CancellationToken ct)
    {
        if (_imageImporter is null || _dialogs is null)
        {
            StatusMessage = "خدمة استيراد الصور غير مهيأة";
            return;
        }

        string? filePath = await _dialogs.PickOpenImageAsync(ct);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            AssetId assetId = await _imageImporter.ImportImageFileAsync(filePath, ct);
            await OpenScannerWithAssetAsync(assetId, ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "تم إلغاء فتح الصورة في الماسح";
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذر فتح الصورة في الماسح: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetSelectedScanRegionToFullImage()
    {
        if (!IsScannerToolsEnabled ||
            SelectedScannerRegionIndex < 0 ||
            SelectedScannerRegionIndex >= ScannerRegions.Count ||
            _scannerSourceFullWidthPx <= 1 ||
            _scannerSourceFullHeightPx <= 1)
        {
            return;
        }

        double maxW = _scannerSourceFullWidthPx - 1.0;
        double maxH = _scannerSourceFullHeightPx - 1.0;
        var fullQuad = new DocumentQuadPx(
            new PointPx(0.0, 0.0),
            new PointPx(maxW, 0.0),
            new PointPx(maxW, maxH),
            new PointPx(0.0, maxH));

        DetectedDocumentRegion current = ScannerRegions[SelectedScannerRegionIndex];
        ScannerRegions[SelectedScannerRegionIndex] = current with { Quad = fullQuad };
        OnPropertyChanged(nameof(ScannerRegions));
        RefreshScannerRectifiedPreview();
        ScannerStatusText = "تم ضبط الإطار على كامل مساحة الصورة";
    }

    [RelayCommand]
    private async Task RedetectScanRegionsAsync(CancellationToken ct)
    {
        if (_scannerCoordinator is null || _scannerSourceAssetId.IsEmpty)
        {
            return;
        }

        await RunDetectionForCurrentAssetAsync(ScanDetectionMode.AutoSingleOrMulti, ct);
    }

    [RelayCommand]
    private void SplitScanCardHorizontal() => SplitSelectedScanCard(IdCardSplitAxis.HorizontalTopBottom);

    [RelayCommand]
    private void SplitScanCardVertical() => SplitSelectedScanCard(IdCardSplitAxis.VerticalLeftRight);

    [RelayCommand]
    private void AddManualScanRegion()
    {
        if (_scannerSourceFullWidthPx <= 0 || _scannerSourceFullHeightPx <= 0)
        {
            return;
        }

        DocumentQuadPx quad = DocumentQuadPx.CreateInsetDefault(_scannerSourceFullWidthPx, _scannerSourceFullHeightPx, marginRatio: 0.14);
        DocumentScanFilterKind filter = SelectedScanFilterOption?.Filter ?? DocumentScanFilterKind.MagicColor;
        ScannerRegions.Add(new DetectedDocumentRegion(
            Guid.NewGuid(),
            $"بطاقة / مستند #{ScannerRegions.Count + 1}",
            quad,
            Confidence: 1.0,
            Filter: filter));
        SelectedScannerRegionIndex = ScannerRegions.Count - 1;
        OnPropertyChanged(nameof(ScannerRegions));
        OnPropertyChanged(nameof(CanRemoveScanRegion));
        RefreshScannerRectifiedPreview();
    }

    [RelayCommand]
    private void RemoveSelectedScanRegion()
    {
        if (!CanRemoveScanRegion)
        {
            return;
        }

        int idx = SelectedScannerRegionIndex;
        ScannerRegions.RemoveAt(idx);
        SelectedScannerRegionIndex = Math.Clamp(idx, 0, ScannerRegions.Count - 1);
        OnPropertyChanged(nameof(ScannerRegions));
        OnPropertyChanged(nameof(CanRemoveScanRegion));
        RefreshScannerRectifiedPreview();
    }

    [RelayCommand]
    private void ApplyScanFilterToAllRegions()
    {
        DocumentScanFilterKind filter = SelectedScanFilterOption?.Filter ?? DocumentScanFilterKind.MagicColor;
        for (int i = 0; i < ScannerRegions.Count; i++)
        {
            ScannerRegions[i] = ScannerRegions[i] with { Filter = filter };
        }

        OnPropertyChanged(nameof(ScannerRegions));
        RefreshScannerRectifiedPreview();
        ScannerStatusText = $"تم تعميم فلتر «{DocumentScanFilterCatalog.GetTitleAr(filter)}» على جميع الإطارات";
    }

    [RelayCommand]
    private async Task InsertScannedToFreeformAsync(CancellationToken ct)
    {
        if (_scannerCoordinator is null || _scannerSourceAssetId.IsEmpty)
        {
            return;
        }

        IReadOnlyList<ScanExtractionRequestItem> items = BuildExtractionRequests();
        if (items.Count == 0)
        {
            return;
        }

        ScannerInsertionOutcome outcome = await _scannerCoordinator.ExtractAndInsertToFreeformAsync(_scannerSourceAssetId, items, ct);
        ScannerStatusText = outcome.MessageAr;
        StatusMessage = outcome.MessageAr;
        if (outcome.Succeeded)
        {
            ShowToast("الماسح الذكي", outcome.MessageAr, global::Avalonia.Controls.Notifications.NotificationType.Success);
            CloseDocumentScanner();
        }
    }

    [RelayCommand]
    private async Task InsertScannedToCollageAsync(CancellationToken ct)
    {
        if (_scannerCoordinator is null || _scannerSourceAssetId.IsEmpty)
        {
            return;
        }

        IReadOnlyList<ScanExtractionRequestItem> items = BuildExtractionRequests();
        if (items.Count == 0)
        {
            return;
        }

        ScannerInsertionOutcome outcome = await _scannerCoordinator.ExtractAndInsertToCollageAsync(
            _scannerSourceAssetId,
            items,
            _session.SelectedCollageSlotId,
            ct);
        ScannerStatusText = outcome.MessageAr;
        StatusMessage = outcome.MessageAr;
        if (outcome.Succeeded)
        {
            ShowToast("الماسح الذكي", outcome.MessageAr, global::Avalonia.Controls.Notifications.NotificationType.Success);
            CloseDocumentScanner();
        }
    }

    [RelayCommand]
    private void CancelScannerOperation()
    {
        _scannerCoordinator?.CancelActiveOperation();
        ScannerStatusText = "جارٍ إلغاء معالجة الماسح الضوئي...";
    }

    [RelayCommand]
    private void CloseDocumentScanner()
    {
        if (IsScannerProcessing)
        {
            return;
        }

        IsDocumentScannerOpen = false;
        _scannerSourceAssetId = AssetId.Empty;
        _scannerSourceFullWidthPx = 0;
        _scannerSourceFullHeightPx = 0;
        ScannerRegions.Clear();
        SKBitmap? oldSource = _scannerSourceBitmap;
        SKBitmap? oldPreview = _scannerRectifiedPreviewBitmap;
        _scannerSourceBitmap = null;
        _scannerRectifiedPreviewBitmap = null;
        ScannerPreviewSummaryText = string.Empty;
        OnPropertyChanged(nameof(ScannerSourceBitmap));
        OnPropertyChanged(nameof(ScannerRectifiedPreviewBitmap));
        OnPropertyChanged(nameof(CanRemoveScanRegion));
        DisposeBitmapAfterCompositorPass(oldSource);
        DisposeBitmapAfterCompositorPass(oldPreview);
    }

    private async Task OpenDocumentScannerCoreAsync(CancellationToken ct)
    {
        AssetId? candidate = TryGetSelectedOriginalOrFirstImageAsset();
        if (candidate is { IsEmpty: false } assetId)
        {
            await OpenScannerWithAssetAsync(assetId, ct);
            return;
        }

        await LoadScannerImageFromFileAsync(ct);
    }

    public async Task OpenScannerWithAssetAsync(AssetId assetId, CancellationToken ct = default)
    {
        if (_scannerCoordinator is null || _assetStore is null || assetId.IsEmpty)
        {
            return;
        }

        if (!_assetStore.TryGetEncodedBytes(assetId, out byte[]? encodedBytes) || encodedBytes is null)
        {
            StatusMessage = "تعذر العثور على الصورة المطلوبة للمسح الضوئي";
            return;
        }

        _scannerSourceAssetId = assetId;
        SKBitmap? oldSource = _scannerSourceBitmap;
        _scannerSourceBitmap = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedBytes, maxEdgePx: 1400);
        OnPropertyChanged(nameof(ScannerSourceBitmap));
        DisposeBitmapAfterCompositorPass(oldSource);

        IsDocumentScannerOpen = true;
        await RunDetectionForCurrentAssetAsync(ScanDetectionMode.AutoSingleOrMulti, ct);
    }

    private async Task RunDetectionForCurrentAssetAsync(ScanDetectionMode mode, CancellationToken ct)
    {
        if (_scannerCoordinator is null || _scannerSourceAssetId.IsEmpty)
        {
            return;
        }

        ScannerDetectionOutcome outcome = await _scannerCoordinator.DetectAsync(_scannerSourceAssetId, mode, ct);
        if (!outcome.Succeeded || outcome.Detection is not { } detection)
        {
            ScannerStatusText = outcome.MessageAr;
            StatusMessage = outcome.MessageAr;
            return;
        }

        _scannerSourceFullWidthPx = detection.ImageWidthPx;
        _scannerSourceFullHeightPx = detection.ImageHeightPx;
        ScannerRegions.Clear();
        foreach (DetectedDocumentRegion region in detection.Regions)
        {
            ScannerRegions.Add(region);
        }

        SelectedScannerRegionIndex = ScannerRegions.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(ScannerRegions));
        OnPropertyChanged(nameof(CanRemoveScanRegion));
        RefreshScannerRectifiedPreview();
        ScannerStatusText = outcome.MessageAr;
        StatusMessage = outcome.MessageAr;
    }

    private void SplitSelectedScanCard(IdCardSplitAxis axis)
    {
        if (!IsScannerToolsEnabled || SelectedScannerRegionIndex < 0 || SelectedScannerRegionIndex >= ScannerRegions.Count)
        {
            return;
        }

        DetectedDocumentRegion target = ScannerRegions[SelectedScannerRegionIndex];
        (DocumentQuadPx first, DocumentQuadPx second) = target.Quad.SplitForIdCard(axis);
        string firstLabel = axis == IdCardSplitAxis.HorizontalTopBottom ? "بطاقة الهوية (أعلى)" : "بطاقة الهوية (أ)";
        string secondLabel = axis == IdCardSplitAxis.HorizontalTopBottom ? "بطاقة الهوية (أسفل)" : "بطاقة الهوية (ب)";

        int idx = SelectedScannerRegionIndex;
        ScannerRegions[idx] = new DetectedDocumentRegion(Guid.NewGuid(), firstLabel, first, target.Confidence, target.Filter);
        ScannerRegions.Insert(idx + 1, new DetectedDocumentRegion(Guid.NewGuid(), secondLabel, second, target.Confidence, target.Filter));
        SelectedScannerRegionIndex = idx;
        OnPropertyChanged(nameof(ScannerRegions));
        OnPropertyChanged(nameof(CanRemoveScanRegion));
        RefreshScannerRectifiedPreview();
        ScannerStatusText = "تم تقسيم بطاقة الهوية إلى إطارين مستقلين";
    }

    private IReadOnlyList<ScanExtractionRequestItem> BuildExtractionRequests()
    {
        if (ScannerRegions.Count == 0)
        {
            return [];
        }

        if (InsertAllScannedRegions)
        {
            return ScannerRegions
                .Select(r => new ScanExtractionRequestItem(r.Id, r.LabelAr, r.Quad, r.Filter))
                .ToArray();
        }

        int idx = Math.Clamp(SelectedScannerRegionIndex, 0, ScannerRegions.Count - 1);
        DetectedDocumentRegion selected = ScannerRegions[idx];
        return [new ScanExtractionRequestItem(selected.Id, selected.LabelAr, selected.Quad, selected.Filter)];
    }

    private AssetId? TryGetSelectedOriginalOrFirstImageAsset()
    {
        if (IsCollageSlotWithImageSelected && SelectedCollageSlot is { } slot)
        {
            return slot.OriginalAssetId ?? slot.AssignedAssetId;
        }

        if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode img)
        {
            return img.OriginalAssetId;
        }

        return ActivePage.Nodes.OfType<ImageNode>().FirstOrDefault()?.OriginalAssetId;
    }

    private void OnScannerCoordinatorStateChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsScannerProcessing));
            OnPropertyChanged(nameof(IsScannerToolsEnabled));
            if (_scannerCoordinator is { IsProcessing: true } coord)
            {
                ScannerStatusText = coord.ProcessingStatusAr;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise);
        }
    }

    private static void DisposeBitmapAfterCompositorPass(SKBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => bitmap.Dispose(), DispatcherPriority.Background);
        }
        else
        {
            bitmap.Dispose();
        }
    }

    private void DisposeScannerResources()
    {
        if (_scannerCoordinator is not null)
        {
            _scannerCoordinator.StateChanged -= OnScannerCoordinatorStateChanged;
        }

        SKBitmap? oldSource = _scannerSourceBitmap;
        SKBitmap? oldPreview = _scannerRectifiedPreviewBitmap;
        _scannerSourceBitmap = null;
        _scannerRectifiedPreviewBitmap = null;
        oldSource?.Dispose();
        oldPreview?.Dispose();
    }
}
