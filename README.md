# Ziwa Studio (زِيوا)

> **استوديو تصميم وطباعة مكتبي أصيل مبنيّ من الصفر بـ `.NET 10 (LTS) + Avalonia UI 11 + SkiaSharp/HarfBuzz`**، وريث الخبرة الهندسية من مشروع **Grido Studio** دون قيود المتصفح أو WebView أو DOM.

---

## الرؤية المعمارية في سطرين
1. **محرك رسم واحد للشاشة والمطبعة (`Preview == Export == Print`):** نفس شجرة المشهد (`SceneNode`) ترسم التفاعل على GPU بسرعة `60–120fps` وتُخرِج ملفات الطباعة (`300–600 DPI` PNG/JPEG، CMYK TIFF، PDF متجه، وWin32 GDI) بواحد-لواحد دون ترقيع DOM أو نقل Base64.
2. **عربي-أول (`RTL-First`) مع معايير Fluent 2 الصارمة:** واجهة `.axaml` بأكملها مبنية على `FlowDirection="RightToLeft"` و`CompiledBindings` وتوكنز موحّدة، ونصوص عربية متقنة التشكيل والانحناء عبر `HarfBuzzSharp`.

---

## خريطة الوثائق المرجعية

| المستند | الغرض |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | البنية الطبقية للمحلول (`Ziwa.sln`)، وحدود الاعتماد، وتدفق البيانات والأوامر |
| [`docs/GRIDO_LESSONS_AND_INVARIANTS.md`](docs/GRIDO_LESSONS_AND_INVARIANTS.md) | خلاصة دروس **Grido Studio** المترجمة إلى ثوابت وقواعد إلزامية في Ziwa |
| [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md) | خارطة التطوير المرحلية (Vertical Slice → MVP → Feature Parity) + مؤشر الاستئناف الحي |
| [`docs/DESIGN_SYSTEM.md`](docs/DESIGN_SYSTEM.md) | نظام واجهة Fluent 2 RTL في Avalonia، التوكنز، الاختصارات، وتجربة الانتظار |
| [`docs/RENDERING_AND_PRINT_SPEC.md`](docs/RENDERING_AND_PRINT_SPEC.md) | محرك SkiaSharp، وحدة المليمتر المعيارية، القص غير الهدّام، الأقنعة، CMYK/PDF/GDI |
| [`docs/SECURITY_AND_CLOUD.md`](docs/SECURITY_AND_CLOUD.md) | الحفظ الذري، SQLite، الخادم المحلي وجسر الهاتف، مسار AI (ONNX + Modal)، Supabase RLS |
| [`.agents/AGENTS.md`](.agents/AGENTS.md) | دستور الوكلاء الأذكياء المقتضب + فهرس القواعد والمهارات الخمس |
| [`changelog/README.md`](changelog/README.md) | معمارية التوثيق الرشيق (`Fragment-First`) بدل تضخم سجل التغييرات اليدوي |

---

## المتطلبات المحلية للبدء
> **حالة البيئة المحلية:** تم تثبيت **.NET 10 SDK (`10.0.404`)** وتأسيس الحل الكامل `Ziwa.sln` بالمشاريع الـ 11 مع اجتياز المرحلة 0 (الشريحة العمودية).

1. ثبّت **.NET 10 SDK (LTS)**:
   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```
2. ثبّت قوالب Avalonia وإضافة التطوير (اختياري لبيئة JetBrains Rider / VS 2022 / VS Code + Avalonia Extension):
   ```powershell
   dotnet new install Avalonia.Templates
   ```
3. لتشغيل بوابة التوثيق وفحص المراجع (تعتمد على Node.js الموجود مسبقاً دون أي حزم خارجية):
   ```powershell
   node scripts/docs-gate.mjs --strict-refs
   ```

---

## أوامر البناء والتحقق المعتمدة (بعد إنشاء `Ziwa.sln`)
```powershell
dotnet format Ziwa.sln --verify-no-changes
dotnet build Ziwa.sln -c Release --no-restore
dotnet test Ziwa.sln --filter "FullyQualifiedName~<TargetSuite>" --no-build
node scripts/docs-gate.mjs --strict-refs
```
