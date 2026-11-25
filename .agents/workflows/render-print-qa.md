# مسار عمل فحص الرسم والطباعة (`Render & Print QA Workflow`)

> **المرجع:** [`.agents/skills/skia-print-pipeline/SKILL.md`](../skills/skia-print-pipeline/SKILL.md) و[`docs/RENDERING_AND_PRINT_SPEC.md`](../../docs/RENDERING_AND_PRINT_SPEC.md).

## متى يُستخدم؟
عند أي تعديل في `Ziwa.Rendering` أو `CropViewportMath` أو `HarfBuzzArabicShaper` أو المصدّرات (PNG/JPEG/CMYK TIFF/PDF/GDI).

## الخطوات
1. **تحقّق من المصدر الواحد:** تأكد أن الشاشة والتصدير والطباعة يستدعون نفس `SceneRenderer.RenderPage` ونفس `MmPerInch = 25.4`.
2. **فحص حالات الحافة:**
   - نص عربي بالتشكيل + إنجليزي مختلط (`BiDi`) + نص منحنٍ على مسار.
   - صورة مقصوصة بـ `CropZoom/Pan` + قناع من الأقنعة الثمانية + فلتر لون + ظل.
   - أبعاد شبكة تتجاوز الورقة (تحجيم نسبي موجب `>= 1mm` داخل الورقة).
3. **فحص التطابق والموارد:** شغّل اختبارات `Ziwa.Rendering.Tests` (`Verify.Xunit`) وتأكد من تغليف موارد `SKSurface`/`SKImage` بـ `using` وعدم الاحتفاظ بـ Decodes 40MP للمعاينة.
4. **تصدير ومراجعة:** تحقق من ترويسات `DPI` (`pHYs`/`JFIF`) ومن مسار `CutContour` المتجه في مخرج PDF.
