---
trigger: glob
glob: '{**/Ziwa.Rendering/**/*.cs,**/Ziwa.Printing/**/*.cs,**/Rendering/**/*.cs,**/Export/**/*.cs,**/Ziwa.Domain/Math/**/*.cs}'
description: 'ثوابت محرك SkiaSharp وHarfBuzz والنصوص العربية والتصدير 300+ DPI وCMYK/PDF/GDI'
---

# قواعد محرك SkiaSharp والطباعة فائقة الدقة

المصدر التفصيلي: `.agents/skills/skia-print-pipeline/SKILL.md` و`docs/RENDERING_AND_PRINT_SPEC.md`.

- **قاعدة التطابق المطلق (`Preview == Export == Print`):** يمنع كتابة شيفرة رسم خاصة بالشاشة وأخرى للتصدير. `SceneRenderer.RenderPage` يرسم المشهد كاملاً فوق أي `SKCanvas` مع تمرير `RenderOptions` (دقة العرض أو 300/600 DPI وإظهار/إخفاء أدلة التحرير).
- **وحدة الحقيقة المليمترية:** الثابت الوحيد للتحويل هو `MmPerInch = 25.4` في `Ziwa.Domain.Math`. كل أبعاد الورقة والكولاج والنزيف و`CutContour` تبقى بالمليمتر حتى لحظة الرسم.
- **تسلسل القناع والمرشحات الثابت:** (1) مرشحات البكسل → (2) قص القناع الهندسي (`MaskPathBuilder`) أو كادر القص (`CropViewportMath`) → (3) خلفية وborder → (4) ظل خارجي واحد. لا يوضع الظل قبل القص فينقطع ولا يُكرر الظل.
- **النصوص العربية (`HarfBuzzSharp`):** استخدم تشكيل `HarfBuzz` لجميع النصوص في الكانفاس (المستقيمة والمنحنية على `SKPath`) لضمان سلامة اللام ألف والتشكيل والـ BiDi في العرض والـ PDF المتجه.
- **انضباط الذاكرة والتخلص (`Dispose`):** لا تحتفظ بصور مفككة بالحجم الكامل للمعاينة الدائمة؛ استخدم `DecodedBitmapCache` للمعاينة (`<= 2048px`) والأصل الكامل أو البلاطات (`Tiled Render`) عند التصدير. غلّف كل `SKPaint` / `SKPath` / `SKImage` / `SKSurface` بـ `using` حتمي.
- **مخرجات الطباعة:** `Vector PDF` يحفظ النصوص والأشكال و`CutContour` كمسارات متجهة. `CMYK TIFF` يمر عبر `Magick.NET` بملف ICC فعلي. الطباعة على ويندوز تمر بـ GDI Spooler بدقة المليمتر الحقيقية مع فحص أخطاء صريح.
