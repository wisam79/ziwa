---
name: ziwa-qa-and-release
description: إستراتيجية الاختبارات المستهدفة، التطابق البصري Verify.Xunit، اختبارات Avalonia.Headless، وبوابات الإصدار وCI
---

# الجودة والاختبارات والإصدار (`Ziwa QA & Release`)

---

## 1. هرم الاختبارات المحلي والسحابي
- **القاعدة المحلية:** شغّل الاختبارات المستهدفة فقط بـ `--filter` أثناء التطوير لتسريع الدورات دون إجهاد الجهاز؛ واترك الحزمة الكاملة والتغطية الشاملة لـ GitHub Actions (`CI`).
- **`Ziwa.Domain.Tests`:** اختبارات وحدة نقية سريعة (mm↔px، محاذاة الشبكات الفيزيائية، حدود `SchemaValidator`، تسلسل الأوامر).
- **`Ziwa.Rendering.Tests` (`Verify.Xunit`):**
  - مقارنة تطابق العرض مع `300 DPI` Export (بصمة/صور مرجعية للنصوص العربية بالتشكيل، الأقنعة الثمانية، `CropZoom/Pan`، النص المنحني).
  - اختبار سلامة ترويسات `pHYs` / `JFIF` ومسار `CutContour` في الـ PDF.
- **`Ziwa.App.Tests` (`Avalonia.Headless.XUnit`):**
  - اختبار فتح المفتش السياقي، عكس `FlowDirection="RightToLeft"`، اختصارات الكيبورد من `CommandRegistry`، وتدفق حالات الانتظار/الإلغاء دون سباقات قديمة.
- **`Ziwa.Infrastructure.Tests`:**
  - محاكاة كتابة ذرية (`AtomicFileWriter`) وتنظيف `.tmp`، سباقات تحديث توكن Supabase عبر خادم HTTP وهمي، وجسر الهاتف المحدود السقف.

---

## 2. الأوامر المعتمدة
```powershell
dotnet format Ziwa.sln --verify-no-changes
dotnet build Ziwa.sln -c Release --no-restore
dotnet test Ziwa.sln --filter "FullyQualifiedName~<TargetClassOrNamespace>" --no-build
node scripts/docs-gate.mjs --strict-refs
```

---

## 3. حزم التوزيع والمحدّث الآمن
- **المثبّت:** **Velopack** (أو InnoSetup مع رمز ترقية/إلغاء تثبيت نظيف يحفظ بيانات المشاريع الشخصية عند التحديث).
- **سلامة الإصدار:**
  1. مطابقة رقم الإصدار في `Directory.Build.props` مع المثبّت.
  2. توليد `ziwa-checksums.txt` وتوقيع التحديث (إذا فُعّل) والتحقق في المحدث بتطبيق **Fail-Closed SHA-256** (لا يُنفذ تثبيت عند غياب أو اختلاف البصمة).
  3. تجميع الـ `changelog/fragments/*.md` في `CHANGELOG.md` تلقائياً عند الترفيع دون تعديل يدوي.
