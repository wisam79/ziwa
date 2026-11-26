---
type: feat
scope: ai
related_files:
  - src/Ziwa.Domain/Primitives/GeometryAndStyle.cs:1
  - src/Ziwa.Domain/Validation/SchemaValidator.cs:1
  - src/Ziwa.Domain/Scanner/DocumentScannerModels.cs:1
  - src/Ziwa.Rendering/Filters/DocumentScanFilterEngine.cs:1
  - src/Ziwa.Rendering/Filters/ImageFilterPipeline.cs:1
  - src/Ziwa.Application/Abstractions/IDocumentScannerService.cs:1
  - src/Ziwa.Application/Scanner/ScannerDocumentOperations.cs:1
  - src/Ziwa.Application/Scanner/DocumentScannerCoordinator.cs:1
  - src/Ziwa.Infrastructure/Scanner/OpenCvDocumentScanner.cs:1
  - src/Ziwa.App/Controls/ScannerQuadCanvasControl.cs:1
  - src/Ziwa.App/Views/DocumentScannerView.axaml:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Scanner.cs:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:1
---
تنفيذ `P4-03`: بناء نافذة الماسح الذكي للمستندات والبطاقات فوق `OpenCvSharp4` مع الكشف التلقائي لمستند واحد أو عدة بطاقات، سحب الزوايا الأربع بالعدسة المكبّرة (`ScannerQuadCanvasControl`)، أداة تقسيم بطاقات الهوية أفقياً وعمودياً، واستعدال المنظور (`Perspective Warp`).
توحيد الفلاتر الخمسة للمستندات (`MagicColor`, `OtsuBlackAndWhite`, `EnhancedGrayscale`, `RemoveYellowing`, `Sharpen`) في `DocumentScanFilterEngine` وربطها مع `ImageFilterPipeline` لتطابق 100%، مع قفل نظيف لأدوات التعديل أثناء المعالجة وإدراج المستندات في الوضع الحر أو الكولاج بخطوة `Undo` واحدة.
تغطية النطاق والرسم والتنسيق و`OpenCvSharp4` والواجهة باختبارات مستهدفة عبر المشاريع الخمسة.
