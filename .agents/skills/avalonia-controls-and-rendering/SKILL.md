---
name: avalonia-controls-and-rendering
description: >-
  تغطي التوثيق المرجعي لـ Avalonia UI 11.3 في بناء عناصر التحكم المخصصة (`UserControl`, `TemplatedControl`, `Control`, `Panel`)، نظام الخصائص (`StyledProperty`, `DirectProperty`, `AttachedProperty`)، دورة حياة الشجرة المرئية ومنع تسرب الذاكرة، الرسم عالي الأداء عبر GPU (`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`، `DrawingContext`، `CompositionCustomVisualHandler`)، والحركات والانتقالات. تُفعّل عند إنشاء أو تعديل أي عنصر مخصص في `Ziwa.App/Controls/` أو ربط سطح رسم Skia أو تشخيص تسرب ذاكرة أو بطء إطارات.
---

# مهارة العناصر المخصصة والرسم عالي الأداء في Avalonia 11.3 (`avalonia-controls-and-rendering`)

المراجع الحية في المشروع: [`SkiaViewportHost.cs`](../../../src/Ziwa.App/Controls/SkiaViewportHost.cs) · [`CanvasRulerControl.cs`](../../../src/Ziwa.App/Controls/CanvasRulerControl.cs) · [`StudioSliderRow.cs`](../../../src/Ziwa.App/Controls/StudioSliderRow.cs) · [`StudioNumberField.cs`](../../../src/Ziwa.App/Controls/StudioNumberField.cs).

---

## 1. مصفوفة اختيار فئة الأساس (`Control` Base Class)

| الفئة الأساسية | متى تختارها؟ | أمثلة في `Ziwa.App` |
|---|---|---|
| `UserControl` | شاشة أو شريط مركّب من عناصر XAML قياسية مرتبطة بـ `ViewModel` (`x:DataType`) | `CommandBarView`, `InspectorPanelView` |
| `TemplatedControl` | عنصر قابل لإعادة الاستخدام بلا مظهر ثابت (`Lookless`)؛ مظهره يعيش في `ControlTheme` | `StudioDialogShell`, `StudioCard`, `StudioSliderRow`, `StudioNumberField` |
| `Control` | عنصر يرسم مباشرة عبر `DrawingContext` أو `ICustomDrawOperation` على سطح GPU | `SkiaViewportHost`, `CanvasRulerControl` |
| `Panel` | حاوية تتحكم بهندسة قياس وترتيب أبنائها (`MeasureOverride` / `ArrangeOverride`) | حاويات تخطيط شبكي أو حر مخصصة |

---

## 2. مصفوفة نظام الخصائص (`AvaloniaProperty`)

| نوع الخاصية | التخزين | الدعم | متى تستخدمه؟ |
|---|---|---|---|
| `StyledProperty<T>` | نظام خصائص Avalonia | الأنماط، `Transitions`، الوراثة، `TwoWay`، `AffectsRender/Measure` | خصائص قابلة للتنسيق أو الربط في `TemplatedControl`/`Control` (مثل `ViewportProperty`, `ValueProperty`) |
| `DirectProperty<TOwner, T>` | حقل C# مباشر (`SetAndRaise`) | ربط سريع `TwoWay`/`OneWay` بلا كلفة تقييم أنماط | حالات عالية التردد أو خصائص للقراءة فقط (`IsPointerOver` داخلي، عدادات فورية) |
| `AttachedProperty<T>` | مملوكة لصنف آخر (`RegisterAttached`) | إسناد سلوك أو بيانات وصفية لأي عنصر (مثل `Container.Name`) | توسيع سلوك عناصر قياسية دون توريثها |

---

## 3. مصفوفة مسارات الرسم (`Rendering Pipelines`)

1. **`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` (المعيار الإلزامي للكانفاس):**
   - يُمرّر عبر `context.Custom(...)` داخل `Control.Render(DrawingContext context)` ليُرسم مباشرة على `SKCanvas` الخاص بـ GPU في Avalonia كما في [`SkiaViewportHost.cs`](../../../src/Ziwa.App/Controls/SkiaViewportHost.cs).
   - **ممنوع منعاً باتاً** استخدام `WriteableBitmap` مع نسخ بايتات CPU في كل إطار للكانفاس التفاعلي.
2. **`DrawingContext` المباشر:**
   - مثالي للكروم الخفيف والخطوط والتدرجات البسيطة دون حاجة لميزات Skia المتقدمة (مثل [`CanvasRulerControl.cs`](../../../src/Ziwa.App/Controls/CanvasRulerControl.cs)).
3. **`CompositionCustomVisualHandler`:**
   - يعمل بالكامل على خيط الرسم (`Render Thread`) عبر `Compositor.CreateCustomVisual(handler)` للحركات المستمرة المستقلة عن خيط الواجهة.

---

## 4. ثوابت منع تسرب الذاكرة ودورة الحياة

- **لا تضع `DataContext = this` داخل `TemplatedControl`:** ذلك يكسر وراثة الـ `DataContext`؛ استعمل `{TemplateBinding ...}` أو `TemplatedParent`.
- **نظّف قبل الربط في `OnApplyTemplate`:** فك اشتراك الأحداث من الأجزاء السابقة (`_textBox`, `_scrubHandle`, `_resetTarget`) قبل ربط الأجزاء الجديدة من `e.NameScope.Find<T>("PART_...")`.
- **التناظر الإلزامي للارتباط بالشجرة المرئية:** كل اشتراك بخدمة خارجية أو `IDisposable` في `OnAttachedToVisualTree` يُفكّ في `OnDetachedFromVisualTree`؛ وكل اشتراك بالـ `ViewModel` في نافذة يُفكّ عند تبدل `DataContextChanged` وعند `Closed` كما في [`MainWindow.axaml.cs`](../../../src/Ziwa.App/Views/MainWindow.axaml.cs).
- **`AddClassHandler` بلا إغلاق على مثيل:** يجب أن يعمل على معامل `sender` فقط دون التقاط `this` أو مراجع محلية طويلة العمر.
- **`Dispatcher.UIThread`:** استعمل `Post` أو `InvokeAsync` (مع `DispatcherPriority.Background` للأعمال غير العاجلة) لتحديث الواجهة من خيوط الخلفية.

---

## 5. التحقق الآلي والمراجع التفصيلية المتدرجة (`references/`)

- فحص قواعد عناصر التحكم و`ISkiaSharpApiLeaseFeature` ودورة الحياة آلياً: `node scripts/avalonia-audit.mjs` عبر [`scripts/avalonia-audit.mjs`](../../../scripts/avalonia-audit.mjs).
- [نظام الخصائص ودورة الحياة ومنع التسرب](references/property-system-and-lifecycle.md): تفاصيل `StyledProperty`/`DirectProperty`/`AttachedProperty`، قواعد `PART_*` و`PseudoClasses`، وتناظر `OnAttachedToVisualTree`/`OnDetachedFromVisualTree`.
- [الرسم المخصص وتكامل Skia والحركات](references/custom-rendering-and-skia-interop.md): تشريح `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`، لقطات الحالة الثابتة للرسم، `CompositionCustomVisualHandler`، `MeasureOverride`/`ArrangeOverride`، و`Transitions`.
