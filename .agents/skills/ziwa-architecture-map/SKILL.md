---
name: ziwa-architecture-map
description: خريطة معمارية حل Ziwa Studio (.NET + Avalonia + SkiaSharp) وتدفق البيانات والأوامر والإحداثيات ونظام Undo/Redo
---

# خريطة معمارية Ziwa Studio

المرجع الكامل للتفاصيل: [`docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md) و[`docs/GRIDO_LESSONS_AND_INVARIANTS.md`](../../../docs/GRIDO_LESSONS_AND_INVARIANTS.md).

---

## 1. المشاريع الستة وحدود الاستدعاء
1. **`Ziwa.Domain`:** كيانات نقية (`DocumentModel`, `PageModel`, `SceneNode`), وحدات القياس (`MmPerInch = 25.4`), مواصفات قوالب الهوية والكولاج والملصقات، وفحص صحة المدخلات (`SchemaValidator`). **صفر مكتبات خارجية.**
2. **`Ziwa.Rendering`:** محرك الرسم (`SceneRenderer`, `SkiaViewportHost` renderer, `HarfBuzzArabicShaper`, `MaskPathBuilder`, `CropViewportMath`, `DecodedBitmapCache`) والمصدّرات (`PngJpegExporter`, `VectorPdfExporter`, `CmykTiffExporter`). **يعتمد على `Ziwa.Domain` فقط.**
3. **`Ziwa.Application`:** تنسيق أوامر المحرر (`CommandRegistry`), سجل التراجع/الإعادة (`UndoStack` عبر `DeltaCommand` و`AssetId`), إدارة حالة الورشة، وواجهات التجريد (`IProjectStore`, `IPrintSpooler`, `IAiUpscaler`). **صفر `Avalonia` أو `File.IO`.**
4. **`Ziwa.Infrastructure`:** `AtomicFileWriter`, `SqliteProjectRepo`, `CrashGuard`, `SupabaseAuthClient`, `ModalAiUpscaler`, `OnnxBackgroundRemover`, `OpenCvDocumentDetector`, `LocalBridgeServer`.
5. **`Ziwa.Platform.Windows`:** `GdiPrintSpooler`, `NativeOleImageDrop`, `SingleInstanceGuard`, تكامل إطار النافذة (Mica/Frameless).
6. **`Ziwa.App`:** `Tokens.axaml`, `Views`, `ViewModels` (`CommunityToolkit.Mvvm`), `SkiaViewportHost` (`ICustomDrawOperation`).

---

## 2. أنماط منع الازدواجية (دروس Grido المطبّقة مسبقاً)
- **ثوابت الإلغاء والمحدودية (`Ziwa.Domain`):** مصدر واحد لـ `Math.Clamp`، وحماية الأرقام غير المنتهية (`AssertFinitePositive`)، وإلغاء العمليات (`OperationCancellation`).
- **الأوامر الموحدة (`Ziwa.Application.Catalog`):** أي أمر (`Open`, `Export`, `Print`, `ToggleZen`, `MaskWithShape`, `Crop`) يعرّف مرة واحدة (`CommandDescriptor`: Id, Label, Shortcut, IconKey, CanExecute, Execute) ويُستهلك في الشريط العلوي، قائمة السياق، QuickBar، ولوحة `Ctrl+K`.
- **تحديد الهدف وملكية المحاولات:** كل عملية غير متزامنة (AI، مسح مستندات، تصدير، حفظ) تحمل `(OperationId, TargetElementId, PageRevision)` وتُلغي بـ `CancellationToken`; ترفض النتيجة فوراً إذا تغير المشروع أو حُذف/قُفل العنصر المستهدف.
- **مراجعة الاختصارات والتراجع:** كل عملية سحب (تحريك، تحجيم، تدوير، منزلق لون) تُعدّل الحالة الحية بسلاسة وتُنشئ أمر `Undo` واحداً فقط عند `Commit`/`PointerReleased`.
