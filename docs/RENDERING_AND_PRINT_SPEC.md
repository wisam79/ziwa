# مواصفات محرك الرسم والطباعة — Rendering & Pre-Press Spec

> **القانون الأول في Ziwa:** إذا ظهر العنصر أو القناع أو النص العربي بشكل معين في المعاينة (`SkiaViewportHost`)، فسيظهر **بالضبط البكسل-للبكسل** في ملف `300/600 DPI` وPDF وCMYK والمطبعة.

---

## 1. نظام الإحداثيات والوحدات (`Ziwa.Domain.Math`)
- **وحدة الحقيقة الوحيدة هي المليمتر (`mm`):**
  - أبعاد الورقة (`A4 = 210 × 297 mm`، أو مقاس مخصص)، الهوامش، الفجوات (`GapMM`)، النزيف (`BleedMM`)، ومواقع العناصر كلها مخزّنة بالمليمتر أو كنسب من أبعاد الصفحة الفيزيائية (`[0..1]`).
  - ثابت التحويل الوحيد في كل الحل: `public const double MmPerInch = 25.4;`.
  - التحويل إلى بكسل: `PxFromMm(double mm, double dpi) => (mm / MmPerInch) * dpi;`.
- **توافق الكولاج والشبكات الفيزيائية:**
  - إذا تجاوز مجموع الخلايا والفجوات عرض/ارتفاع الورقة بأكثر من حد معتمد، يتم التناسب مع ضمان كل خلية `>= 1mm` وبقاء كامل الشبكة داخل حدود الورقة المطبوعة (موروث ومختبر من `grid-utils` في Grido).

---

## 2. شجرة المشهد وقناة الرسم الموحدة (`Ziwa.Rendering`)
يُطبّق `SceneRenderer.RenderPage(SKCanvas canvas, PageModel page, RenderOptions options)` نفس المراحل الترتيبية في كل المخرجات:

1. **خلفية الورقة والنزيف:** رسم لون/تدرج الورقة داخل حدود `[-BleedMM .. PageWidth + BleedMM]`.
2. **ترتيب الطبقات (`ZIndex` تصاعدي):**
   - `CollageSlotNode` / `ImageNode`:
     - حساب نافذة القص غير الهدّام (`CropZoom`, `CropPanX`, `CropPanY`) عبر دالة نقية واحدة في `CropViewportMath`.
     - تطبيق مرشحات البكسل (`Brightness`, `Contrast`, `Saturation`, `Blur`, `Threshold`, `Pixelate`, `MagicScan`).
     - تطبيق القناع الهندسي (`MaskShape`: `None`, `Circle`, `Arch`, `Hexagon`, `Diamond`, `Heart`, `Star`, `Seal`) عبر `MaskPathBuilder.Build(mask, rect)` — نفس المسار يُصدر كـ SVG لمعاينات الواجهة!
     - رسم الإطار والحد (`Stroke`) ثم ظل واحد نظيف (`DropShadow`) دون تراكب ظلال.
   - `ShapeNode`: يدعم التعبئة باللون أو التدرج أو **تعبئة بصورة (`ImageFill`)** + استدارة زوايا موحدة أو مستقلة (`CornerRadii[4]`).
   - `TextNode`: تشكيل النصوص العربية واللاتينية المختلطة (`BiDi`) بـ `HarfBuzzSharp` عبر `SKShaper` أو `RichText` مخصص، ودعم **النص المنحني على مسار (`DrawTextOnPath`)** والخلفيات والحدود والظلال والأختام.
   - `SvgStickerNode`: استيراد متجه عبر `Svg.Skia` بعد تمريره عبر معقّم SVG (`SvgSanitizer`: إزالة `<script>` و`on*` و`foreignObject` والروابط الخارجية غير الموثوقة) ودعم تلوين الشعار بضغطة واحدة عبر `ColorFilter` أو استبدال `data-color-role`.
3. **طبقة الطباعة النهائية (عند التصدير/الطباعة فقط، لا في معاينة المحرر إلا كدليل):**
   - علامات القص (`CropMarks`)، علامات التسجيل للبلوتر (`RegistrationMarks`)، ومسار **`CutContour`** كطبقة متجهة دقيقة بالمليمتر.

---

## 3. الأداء والذاكرة (`VRAM & Tile Budget`)
- **فك تشفير ثنائي المستوى للأصول (`Two-Tier Decode`):**
  - عند استيراد صورة `40MP` (`6000×4000`)، يُحتفظ بالأصل على القرص في مستودع الأصول (`AssetId`)، وتُفك نسخة معاينة بحد مناسب للعرض (`<= 2048px` على الضلع الأطول) للمحرر السريع، بينما يُقرأ الأصل الكامل لحظة التصدير `300+ DPI` أو عبر **الرسم المجزّأ (`Tiled Render`)** للمقاسات العملاقة لتجنب `OutOfMemoryException`.
- **تخلص حتمي (`Deterministic Dispose`):** كل `SKBitmap`، `SKImage`، `SKSurface`، `SKPaint` مؤقت في حلقة رسم يُغلّف بـ `using` أو يُدار عبر كاش LRU محدد بسقف ذاكرة واضح.

---

## 4. مخرجات التصدير والطباعة
| المخرج | التقنية المعتمدة | الضمان |
|---|---|---|
| **PNG / JPEG** | `SkiaSharp` (`SKSurface` + تضمين بيانات `DPI` في رأس `pHYs`/`JFIF`) | جودة 100% متطابقة مع الكانفاس |
| **Vector PDF** | `SKDocument.CreatePdf` مع `SKCanvas` نفسه | النصوص والأشكال والشعارات و`CutContour` تبقى متجهة حادة مهما كُبّرت |
| **CMYK TIFF** | **Render RGBA High-Res → `Magick.NET` (ImageMagick)** مع ملف تعريف ICC (`USWebCoatedSWOP` / `FOGRA39`) | ألوان مطبعية حقيقية وملف TIFF مضبوط الـ DPI |
| **Direct Windows Print** | **Win32 GDI Spooler** عبر `Ziwa.Platform.Windows` + `PrintTicket` بالمليمتر | تجاهل أي أخطاء صامتة؛ مطابقة أبعاد الورقة والهوامش الفيزيائية |
