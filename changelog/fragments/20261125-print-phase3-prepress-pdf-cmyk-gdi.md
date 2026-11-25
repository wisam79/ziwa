---
type: feat
scope: print
related_files:
  - src/Ziwa.Domain/PrePress/PrePressModels.cs:1
  - src/Ziwa.Application/Pages/MultiPageOperations.cs:1
  - src/Ziwa.Application/PrePress/PreflightChecker.cs:1
  - src/Ziwa.Application/Printing/PrintCalibrationPlanner.cs:1
  - src/Ziwa.Rendering/PrePress/PrintMarksAndCutContourRenderer.cs:1
  - src/Ziwa.Rendering/Export/TiledRasterRenderer.cs:1
  - src/Ziwa.Rendering/Export/VectorPdfExporter.cs:1
  - src/Ziwa.Rendering/Export/CmykTiffExporter.cs:1
  - src/Ziwa.Infrastructure/Export/PageExportService.cs:1
  - src/Ziwa.Platform.Windows/Printing/GdiPrintSpooler.cs:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.cs:1
  - src/Ziwa.App/Views/MainWindow.axaml:1
  - docs/DEVELOPMENT_PLAN.md:1
---
إنجاز المرحلة 3 بالكامل (`P3-01` .. `P3-05`): تعدد الصفحات المبسّط (`MultiPageOperations` وشريط الصفحات السفلي)، علامات القص والتسجيل ومسار البلوتر `CutContour` وفاحص ما قبل الطباعة الهادئ `PreflightChecker`، تصدير `Vector PDF` متعدد الصفحات والتصدير المجزّأ `TiledRasterRenderer` للوحات العملاقة، مُصدِّر `CMYK TIFF` (`Magick.NET` + `ICC` + `LZW`)، ومحرك الطباعة المباشرة على ويندوز `GdiPrintSpooler` مع تغليف `SafeHandle` وحوار الطباعة بالمعايرة المليمترية.
