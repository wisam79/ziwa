---
type: feat
scope: ai
related_files:
  - src/Ziwa.Domain/Ai/CloudAiModels.cs:1
  - src/Ziwa.Application/Abstractions/ICloudAiServices.cs:1
  - src/Ziwa.Application/Ai/CloudAiCoordinator.cs:1
  - src/Ziwa.Application/Collage/CollageOperations.cs:372
  - src/Ziwa.Infrastructure/Ai/CloudAiOptions.cs:1
  - src/Ziwa.Infrastructure/Ai/ModalAiUpscaler.cs:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Ai.cs:1
  - src/Ziwa.App/Views/MainWindow.axaml:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:1
---
تنفيذ `P4-04` (وإغلاق المرحلة 4 بالكامل): بناء عميل الترميم السحابي المزدوج (`ModalAiUpscaler`) المتوافق 100% مع نقطة نهاية Grido القائمة (`Modal CPU`: `YuNet + CodeFormer + 2x`) باستخدام مصادقة `Authorization: Bearer <UserJWT>` حصراً (صفر أسرار مدمجة وفرض `https://`)، وفحص سقف الأبعاد (`<= 2048px`) قبل الإرسال، ومفتاح عدم التكرار المشفّر `UUIDv4` (`Idempotency-Key` / `requestId`)، ومهلة عميل `180s` بلا إعادة محاولة تلقائية مع الحفاظ على شفافية الصور المعزولة وتصفير شارة الحصة عند تسجيل الخروج.
تحديث الصور المرمّمة في الوضع الحر وخانات الكولاج في خطوة `Undo` واحدة مع معايرة `CropZoom/Pan` ومزامنة وضع التكرار عبر `CloudAiCoordinator` و`OperationTicket`، وتغطية جميع المسارات وحالات الفشل (النجاح، الإلغاء، المهلة، `409`, `usage_recorded=false`, وتبديل المستند) باختبارات مستهدفة عبر المشاريع الأربعة.
