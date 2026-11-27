# نظام الخصائص ودورة الحياة ومنع تسرب الذاكرة في Avalonia 11.3

> مرجع تفصيلي مبني على توثيق **Avalonia 11.3** وأنماط التنفيذ الفعلية في [`StudioNumberField.cs`](../../../../src/Ziwa.App/Controls/StudioNumberField.cs) و[`StudioSliderRow.cs`](../../../../src/Ziwa.App/Controls/StudioSliderRow.cs) و[`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs).

---

## 1. تعريف الخصائص وإبطال الرسم/التخطيط (`Affects*`)

### أ. `StyledProperty<T>`
تُدار قيمته بواسطة نظام أسبقية الخصائص في Avalonia (يدعم `Style` و`ControlTheme` و`Transitions` و`Coerce`):
```csharp
public static readonly StyledProperty<double> ValueProperty =
    AvaloniaProperty.Register<StudioNumberField, double>(
        nameof(Value),
        defaultValue: 0.0,
        defaultBindingMode: BindingMode.TwoWay,
        coerce: (_, v) => double.IsFinite(v) ? v : 0.0);

static StudioNumberField()
{
    AffectsRender<StudioNumberField>(ValueProperty);
    // استعمل AffectsMeasure<T>(...) إذا غيّرت الخاصية الأبعاد المطلوبة
    // استعمل AffectsArrange<T>(...) إذا غيّرت الخاصية التموضع فقط
}
```
- **ملاحظة:** استدعِ `AffectsRender<T>` / `AffectsMeasure<T>` / `AffectsArrange<T>` داخل المشيّد الساكن (`static Control()`) بدل كتابة `OnPropertyChanged` اليدوي لمجرد استدعاء `InvalidateVisual()`.

### ب. `DirectProperty<TOwner, T>`
يُخزّن في حقل C# عادي ويتجنب كلفة نظام الأنماط (مثالي للخصائص سريعة التغير أو للقراءة فقط):
```csharp
private bool _isDragging;

public static readonly DirectProperty<MyCanvas, bool> IsDraggingProperty =
    AvaloniaProperty.RegisterDirect<MyCanvas, bool>(
        nameof(IsDragging),
        o => o.IsDragging,
        (o, v) => o.IsDragging = v);

public bool IsDragging
{
    get => _isDragging;
    private set => SetAndRaise(IsDraggingProperty, ref _isDragging, value);
}
```

### ج. `AttachedProperty<T>`
تُضاف عبر `AvaloniaProperty.RegisterAttached<THost, TTarget, TValue>(...)` مع دالتي `Get*` و`Set*` ساكنتين.

---

## 2. تشريح `TemplatedControl` السليم (`PART_*` و`PseudoClasses`)

1. **لا تكسر سياق البيانات:** لا تكتب `DataContext = this;` في مشيّد `TemplatedControl`. استخدم `{TemplateBinding ...}` داخل `ControlTemplate` في [`StudioControlThemes.axaml`](../../../../src/Ziwa.App/Themes/StudioControlThemes.axaml).
2. **سمات التحقق التوثيقية:** أضف `[TemplatePart("PART_TextBox", typeof(TextBox))]` و`[PseudoClasses(":dragging")]` على الصنف عند الحاجة لتوضيح العقد مع القالب.
3. **إدارة `OnApplyTemplate(TemplateAppliedEventArgs e)` بأمان:** قد يُستدعى `OnApplyTemplate` أكثر من مرة عند تبدّل الثيم أو القالب؛ لذا افصل الاشتراكات القديمة دائماً قبل ربط الجديدة (كما في [`StudioNumberField.cs`](../../../../src/Ziwa.App/Controls/StudioNumberField.cs#L98-L120)):
   ```csharp
   protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
   {
       base.OnApplyTemplate(e);
       if (_resetTarget is not null)
       {
           _resetTarget.DoubleTapped -= OnResetDoubleTapped;
       }

       _resetTarget = e.NameScope.Find<Control>("PART_ResetArea");
       if (_resetTarget is not null)
       {
           _resetTarget.DoubleTapped += OnResetDoubleTapped;
       }
   }
   ```
4. **تحديث الحالات الزائفة:** `PseudoClasses.Set(":scrubbing", _isScrubbing);` لتمكين المحددات مثل `^:scrubbing` في `ControlTheme`.

---

## 3. دورة حياة الشجرة المرئية ومنع تسرب الذاكرة

العناصر داخل القوالب أو الألسنة أو القوائم المنبثقة قد تنفصل وتُعاد للشجرة المرئية مراراً؛ أي مرجع ثابت يحتفظ بعنصر منفصل يمنع جامع القمامة (GC) من تحرير الشجرة بأكملها.

### القواعد الأربع لمنع التسرب
1. **زوج `OnAttachedToVisualTree` / `OnDetachedFromVisualTree` المتناظر:**
   - أي اشتراك بأحداث خدمات طويلة العمر أو مثيلات ساكنة أو `GetObservable(Prop).Subscribe(...)` يُنشأ عند الإرفاق، ويُفكّ أو يُتلف (`Dispose()`) حتماً في `OnDetachedFromVisualTree`.
2. **فك اشتراكات `DataContext` والنوافذ:**
   - عند الاشتراك بأحداث `ViewModel` في Code-Behind (مثل `ToastRequested` أو `ViewportFitRequested` في [`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs#L28-L49))، احتفظ بمرجع `_subscribedViewModel` وفكّ اشتراكه عند كل `DataContextChanged` وكذلك داخل `Closed`.
3. **فخ الإغلاق في `AddClassHandler`:**
   - يسجّل `AddClassHandler<TControl>(...)` معالجاً على مستوى الصنف بأكمله. **إياك** أن تمرر دالة لامدا تلتقط `this` أو متغيراً محلياً حياً؛ اعمل حصراً على معامل `sender` الممرّر للمعالج.
4. **فك اشتراكات `OnPropertyChanged` والمؤقتات:**
   - إذا أنشأت `DispatcherTimer` داخل عنصر مخصص، أوقفه (`Stop()`) وافصل `Tick` في `OnDetachedFromVisualTree`.

---

## 4. خيط الواجهة `Dispatcher.UIThread`

- كل قراءة أو تعديل على خصائص عناصر التحكم أو الأشجار المرئية يجب أن يتم على خيط الواجهة.
- **طرق الاستخدام الآمن:**
  - `Dispatcher.UIThread.CheckAccess()` / `VerifyAccess()` للتحقق من الخيط الحالي.
  - `Dispatcher.UIThread.Post(() => ..., DispatcherPriority.Background)` للإرسال غير الحاجز (يفضّل `Background` عند وجود تدفق تحديثات حتى لا تتجمد استجابة الماوس والحركات).
  - `await Dispatcher.UIThread.InvokeAsync(() => ...)` عندما تحتاج لانتظار التنفيذ أو قراءة قيمة ناتجة.
- **سباقات الإلغاء:** كما في [`AvaloniaProjectDialogs.cs`](../../../../src/Ziwa.App/Services/AvaloniaProjectDialogs.cs)، عند إغلاق حوار عبر `CancellationToken` مرّر الإغلاق عبر `Dispatcher.UIThread.Post(...)` بعد التحقق من حالة النافذة.
