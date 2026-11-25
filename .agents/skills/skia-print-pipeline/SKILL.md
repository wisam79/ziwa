---
name: skia-print-pipeline
description: محرك الرسم SkiaSharp وHarfBuzz والنصوص العربية، الأقنعة، القص غير الهدام، وتصدير 300+ DPI / PDF / CMYK TIFF / GDI
---

# خط أنابيب الرسم والطباعة (`Ziwa.Rendering`)

المرجع المواصفاتي: [`docs/RENDERING_AND_PRINT_SPEC.md`](../../../docs/RENDERING_AND_PRINT_SPEC.md).

---

## 1. وحدة المليمتر ومعايرة DPI
- استخدم دائماً `DomainMath.MmPerInch` (`25.4`) و`DomainMath.MmToPx(mm, dpi)`.
- حافظ على تطابق أبعاد الورقة والكولاج عند تصغير الشبكات الطويلة: لا خلية أصغر من `1.0mm` ولا خروج لخلية عن الورقة المطبوعة.

---

## 2. النصوص العربية والعنصر المنحني (`HarfBuzzSharp`)
- لتشكيل العربية بدقة في `SkiaSharp`:
  1. حمّل الخط ضمن `SKTypeface` ولفّه بـ `HarfBuzzSharp.Blob` / `Face` / `Font` (خزّنه في `FontRegistry` دون إعادة إنشائه في كل إطار).
  2. استخدم `SKShaper` (أو `Buffer` في `HarfBuzzSharp` للتحكم بالاتجاه `Direction.RightToLeft` والفواصل) للحصول على `Glyphs` ومواضعها `(XOffset, YOffset, Advance)` بدقة مطبعية.
  3. في **النص على مسار منحني (`TextOnPath`)** للأختام والشارات: احسب طول المسار عبر `SKPathMeasure`، ثم شكّل الكلمات أولاً بـ `HarfBuzz` لتحافظ الحروف العربية على اتصالها، وضَع كل عنقود (`Cluster`) أو خط (`Run`) متصلاً بالزاوية المناسبة أو حوّل الـ Glyphs المتصلة لمسار `SKPath` ليبقى حاداً ومطبوعاً بدقة في Vector PDF!

---

## 3. الأقنعة والقص والـ Filters والـ SVG
- **الترتيب المقدّس لعقد الصور:**
  1. استدعِ `CropViewportMath.ComputeSourceAndDestRect(naturalW, naturalH, slotW, slotH, cropZoom, cropPanX, cropPanY)` للحصول على نافذة القص غير الهدام.
  2. طبّق `SKColorFilter` / `SKImageFilter` (سطوع، تباين، تشبع، ضبابية، أو مرشحات المسح الضوئي) داخل طبقة مؤقتة إذا لزم.
  3. طبّق `canvas.ClipPath(maskPath, antialias: true)` عبر `MaskPathBuilder.Build(maskType, bounds)`.
  4. بعد استعادة الحالة (`canvas.Restore()`) ارسم حدّ القناع الخارجي (`Stroke`) ثم الظل الوحيد.
- **مستندات SVG المستوردة:** تمر وجوباً بـ `SvgSanitizer` قبل تمريرها لـ `Svg.Skia`. دعم «تلوين الشعار» يعمل إما على `SKColorFilter.CreateBlendMode(tintColor, SKBlendMode.SrcIn)` أو باستبدال خصائص اللون المعنونة.

---

## 4. المخرجات عالية الدقة (300–600 DPI)
- **PNG / JPEG:** صدّر عبر `SKSurface.Create(new SKImageInfo(pxW, pxH, ...))`؛ اضبط ترويسة الكثافة المادية (`pHYs` للـ PNG، و`JFIF` للـ JPEG) بالضبط لكي تقرأ برامج الطباعة المقاس بالمليمتر تلقائياً.
- **Vector PDF:** أنشئ `SKDocument.CreatePdf(stream, dpi)` واستدعِ `SceneRenderer.RenderPage(pageCanvas, page, exportOptions)` ثم ارسم طبقة `CutContour` المتجهة بلون مخصص واضح عند طلب خط القص.
- **CMYK TIFF:** ارسم على `SKSurface` بالـ RGBA العالي، مرّر البايتات إلى **`Magick.NET` (`MagickImage`)**، طبّق ملف ICC و`ColorSpace.CMYK` واحفظ TIFF مضغوطاً (LZW/ZIP) مع حقن `Density` بالبوصة.
- **ذاكرة التصدير العملاق:** للأوراق الكبيرة جداً (> 6000×6000 بكسل)، فعّل **Tiled Render** بقطع `2048×2048` مع `SKCanvas.Translate(-tileX, -tileY)` ودمجها عبر `Magick.NET` أو المصدّر دون استنزاف الذاكرة.
