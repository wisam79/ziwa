# الرسم المخصص وتكامل Skia والحركات في Avalonia 11.3

> مرجع عملي لبناء مسطحات الرسم GPU، لوحات القياس والترتيب المخصصة، وحركات الواجهة في `Ziwa.App`.

---

## 1. تكامل Skia المباشر عبر GPU (`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`)

هذا هو النمط المعتمد في [`SkiaViewportHost.cs`](../../../../src/Ziwa.App/Controls/SkiaViewportHost.cs) لتحقيق `60–120fps` ومطابقة البكسلات مع `Ziwa.Rendering`:

```csharp
public override void Render(DrawingContext context)
{
    if (Bounds.Width <= 0 || Bounds.Height <= 0)
        return;

    // مرّر لقطة غير متغيرة (Immutable Snapshot) من الحالة إلى عملية الرسم
    context.Custom(new ViewportDrawOperation(
        new Rect(Bounds.Size),
        Page,
        Viewport,
        SceneRenderer,
        BackgroundColor));
}

private sealed class ViewportDrawOperation : ICustomDrawOperation
{
    public Rect Bounds { get; }
    // حقول القراءة فقط للحالة الملتقطة...

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false; // أو قارن الحقول للثبات
    public void Dispose() { }

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null) return;

        using var lease = leaseFeature.Lease();
        SKCanvas canvas = lease.SkCanvas;

        int saveCount = canvas.Save();
        try
        {
            // ارسم عبر SceneRenderer أو SkiaSharp مع تحرير أي SKPaint/SKPath محلي عبر using
        }
        finally
        {
            canvas.RestoreToCount(saveCount);
        }
    }
}
```

### قواعد صارمة في `ICustomDrawOperation`
1. **لقطة حالة آمنة للخيوط:** قد ينفذ Avalonia دالة `Render(ImmediateDrawingContext)` على خيط الرسم بعد انتهاء دورة التحديث على خيط الواجهة؛ لذا مرّر سجلات غير متغيرة (`ZiwaDocument`, `PageModel`, `ViewportTransform`) ولا تقرأ خصائص `Control` أو قوائم قابلة للتعديل من داخل `ImmediateDrawingContext.Render`.
2. **حفظ واستعادة حالة `SKCanvas`:** استخدم `canvas.Save()` و`canvas.RestoreToCount(saveCount)` داخل `try/finally` حتى لا يتسرب قص (`Clip`) أو مصفوفة تحويل إلى العناصر اللاحقة على نفس السطح.
3. **الإنعاش اليدوي (`InvalidateVisual()`):** تتجاوز `ICustomDrawOperation` تخزين شجرة المشهد القياسي؛ أعلن عن الخصائص في `AffectsRender<T>(...)` واستدعِ `InvalidateVisual()` عند تغير الحالة الداخلية للحساب/السحب.
4. **إدارة الذاكرة الأصلية:** كل `SKPaint`, `SKPath`, `SKImage`, `SKShader`, `SKMaskFilter` يُنشأ داخل عملية الرسم يجب تغليفه بـ `using`.
5. **اختبار `Fallback` بلا Skia:** إذا عاد `context.TryGetFeature<ISkiaSharpApiLeaseFeature>()` بـ `null` (كما في أوضاع `UseHeadlessDrawing = true`)، أرجِع بهدوء دون استثناء.

---

## 2. الرسم الخفيف عبر `DrawingContext` (مثال المساطر)

للأدوات غير المعقدة مثل [`CanvasRulerControl.cs`](../../../../src/Ziwa.App/Controls/CanvasRulerControl.cs) (خطوط المسطرة، الأرقام، خلفية الورقة، علامة التحديد):
- ارسم مباشرة عبر `DrawingContext.DrawRectangle` و`DrawLine` و`DrawText` (`FormattedText`).
- فعّل `ClipToBounds = true;` في المشيّد حتى لا تفيض علامات القياس خارج حد الـ `20px`.
- اربط كل خصائص الفرش والأبعاد بـ `AffectsRender<T>(...)` لضمان إعادة الرسم فور تبديل الثيم الفاتح/الداكن أو تغير `Viewport`.

---

## 3. الحركات المستمرة على خيط الرسم (`CompositionCustomVisualHandler`)

عند الحاجة لحركة مستمرة بسرعة الشاشة (مثل نبض مؤشر أو معاينة حية مستمرة) دون إشغال خيط الواجهة (`UIThread`):
- أنشئ صنفاً يرث من `CompositionCustomVisualHandler` ومرّره إلى `ElementComposition.GetElementVisual(control)?.Compositor.CreateCustomVisual(handler)`.
- تُحدّث هذه الآلية عبر رسائل خفيفة (`SendHandlerMessage`) وتنفذ `OnRender(ImmediateDrawingContext context)` مباشرة على خيط التركيب (`Composition Thread`).

---

## 4. حاويات التخطيط المخصصة (`Panel`: `MeasureOverride` & `ArrangeOverride`)

عند بناء `Panel` مخصص (مثل شبكة معاينة غير قياسية):
1. **`MeasureOverride(Size availableSize)`:**
   - استدعِ `child.Measure(childConstraint)` لكل عنصر في `Children`.
   - تعامل بحذر مع `double.IsInfinity(availableSize.Width)` أو `Height` عند رجوع الحجم المطلوب (`DesiredSize`) حتى لا يعود الـ Panel بـ `Infinity`.
2. **`ArrangeOverride(Size finalSize)`:**
   - استدعِ `child.Arrange(new Rect(x, y, width, height))` لكل ابن ضمن `finalSize` وأعد `finalSize`.

---

## 5. الانتقالات والحركات (`Transitions` و`Animations` و`PageTransitions`)

- **الانتقالات البسيطة (`Transitions`):** مناسبة لتنعيم تفاعل الأزرار والمؤشرات (`DoubleTransition Property="Opacity" Duration="0:0:0.12"` في `AppleHigStyles.axaml`).
- **حركات الإطارات المفتاحية (`Keyframes`):** تُعرّف داخل `<Style.Animations>` لتأثيرات دورية محدودة (مثل وميض خفيف أو دوران مؤشر).
- **انتقالات الصفحات (`PageTransitions`):** في `TransitioningContentControl` أو `Carousel` عبر `CrossFade` و`PageSlide` و`CompositePageTransition`.
- **حدود الأداء في الاستوديو:** حافظ على مدة الانتقالات التفاعلية `<= 150ms`، ولا تضع `Transitions` على خصائص تغيير الأبعاد/الموضع أثناء سحب عناصر الكانفاس أو تحريك المؤشر.
