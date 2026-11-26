---
type: feat
scope: ai
related_files:
  - src/Ziwa.Domain/Ai/IdPhotoFramingSpecs.cs:1
  - src/Ziwa.Rendering/Ai/AlphaMaskCompositor.cs:1
  - src/Ziwa.Application/Abstractions/IOfflineAiServices.cs:1
  - src/Ziwa.Application/Ai/OfflineAiCoordinator.cs:1
  - src/Ziwa.Application/Session/OperationTicket.cs:1
  - src/Ziwa.Application/Session/DocumentSession.cs:1
  - src/Ziwa.Infrastructure/Ai/OnnxBackgroundRemover.cs:1
  - src/Ziwa.Infrastructure/Ai/HairDetailAlphaMatting.cs:1
  - src/Ziwa.Infrastructure/Ai/OnnxFaceFramingService.cs:1
  - src/Ziwa.Infrastructure/Ai/EmbeddedOnnxModelBuilder.cs:1
  - src/Ziwa.Infrastructure/Assets/FileImageAssetImporter.cs:1
  - src/Ziwa.App/Controls/MaskBrushCanvasControl.cs:1
  - src/Ziwa.App/Views/AiMaskEditorView.axaml:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Ai.cs:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:1
---
تنفيذ `P4-02`: دمج `Microsoft.ML.OnnxRuntime` في خيط خلفي لعزل الخلفية أوفلاين مع الحفاظ على تفاصيل الشعر عبر تنعيم القناع (`HairDetailAlphaMatting`) وإلغاء فوري بـ `CancellationToken` و`RunOptions.Terminate`.
إضافة محرر القناع اليدوي `AiMaskEditorView` فوق `MaskBrushCanvasControl` بفرشاة المسح والاستعادة ومعاينة قبل/بعد وتراجع للضربات، وتأطير الوجه التلقائي لصور الهوية (`IdPhotoFramingCatalog`) في الوضع الحر وخانات الكولاج مع حفظ `OriginalAssetId` للاستعادة.
تغطية النطاق والرسم والتنسيق وONNX والواجهة باختبارات مستهدفة عبر المشاريع الخمسة وفحص `FileImageAssetImporter` بالتوقيع السحري.
