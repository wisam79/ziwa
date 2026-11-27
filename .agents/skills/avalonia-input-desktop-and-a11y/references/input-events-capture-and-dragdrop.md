# الأحداث الموجهة والتقاط المؤشر والسحب والإفلات في Avalonia 11.3

> مرجع تفصيلي مبني على توثيق **Avalonia 11.3** وأنماط التفاعل في [`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs) و[`SkiaViewportHost.cs`](../../../../src/Ziwa.App/Controls/SkiaViewportHost.cs) و[`KeyboardCommandRouter.cs`](../../../../src/Ziwa.App/Services/KeyboardCommandRouter.cs).

---

## 1. نموذج الأحداث الموجهة (`Routed Events`: `Tunnel`, `Bubble`, `Direct`)

تنتقل أحداث المؤشر ولوحة المفاتيح في Avalonia عبر الشجرة المرئية على مرحلتين:
1. **`RoutingStrategies.Tunnel` (نفق من الجذر نحو المصدر):** يتلقى الأب الحدث قبل الابن؛ يُستخدم عندما يريد عنصر أب أو نافذة اعتراض مفتاح قبل استهلاكه داخلياً.
   - مثال حي: `InlineCanvasTextBox.AddHandler(KeyDownEvent, OnInlineCanvasTextKeyDown, RoutingStrategies.Tunnel);` في [`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs#L23) لالتقاط `Enter` و`Escape` قبل `TextBox.AcceptsReturn`.
2. **`RoutingStrategies.Bubble` (فقاعة من المصدر إلى الجذر):** المسار الافتراضي لمعظم المعالجات (`OnKeyDown`، `OnPointerPressed`).
3. **إيقاف الانتشار:** اضبط `e.Handled = true` فور معالجة الحدث حتى لا تلتقطه الطبقات الأعلى مرة ثانية.

---

## 2. أحداث المؤشر والتقاط المؤشر (`Pointer Capture`)

### القراءة الدقيقة لحالة المؤشر
```csharp
protected override void OnPointerPressed(PointerPressedEventArgs e)
{
    base.OnPointerPressed(e);
    Point posDip = e.GetPosition(this);
    PointerPointProperties props = e.GetCurrentPoint(this).Properties;

    if (props.IsLeftButtonPressed)
    {
        e.Pointer.Capture(this);
        // ابدأ السحب أو التحجيم...
        e.Handled = true;
    }
}
```
- **النقر المزدوج:** افحص `e.ClickCount == 2` في `OnPointerPressed` (أو حدث `DoubleTapped` للأزرار والأسطح غير المعتمدة على إحداثيات دقيقة) لفتح القص الموضعي أو تحرير النص أو إعادة تعيين المنزلق.
- **مفتاح التثبيت (`Alt`/`Shift`):** اقرأ `e.KeyModifiers` أثناء السحب (`Shift` لتقييد النسبة أو زوايا `15°`، `Alt` لتعطيل `SnapEngine` مؤقتاً).

### القاعدة الإلزامية لـ `OnPointerCaptureLost`
يحتفظ عنصر واحد فقط بالتقاط المؤشر على مستوى التطبيق. قد ينقطع الالتقاط فجأة بسبب نافذة منبثقة، اختصار نظام `Alt+Tab`، أو فقدان التركيز:
- **نفّذ التنظيف دائماً في `OnPointerCaptureLost(PointerCaptureLostEventArgs e)`** (كما في [`StudioNumberField.cs`](../../../../src/Ziwa.App/Controls/StudioNumberField.cs) و[`SkiaViewportHost.cs`](../../../../src/Ziwa.App/Controls/SkiaViewportHost.cs)): أوقف السحب، أزل الأدلة المؤقتة، وأرجع حالة المعاينة دون ترك حالة معلقة.
- حرّر الالتقاط صراحةً عند الانتهاء عبر `e.Pointer.Capture(null)`.

---

## 3. لوحة المفاتيح والتركيز وسلم `Escape`

1. **قابلية التركيز:** العناصر التفاعلية المخصصة مثل `SkiaViewportHost` يجب أن تضبط `Focusable = true;` في المشيّد لتستقبل أحداث المفاتيح وتستعيد التركيز بعد إغلاق الألواح.
2. **حماية حقول الإدخال النصي:** قبل توجيه الاختصار عبر [`KeyboardCommandRouter.cs`](../../../../src/Ziwa.App/Services/KeyboardCommandRouter.cs)، تحقّق:
   ```csharp
   bool textInput = e.Source is TextBox
       || (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>() is not null);
   ```
   حتى لا تلتهم اختصارات `Delete` أو مفاتيح الأدوات كتابة المستخدم داخل `TextBox`.
3. **سلم إغلاق `Escape` الهادئ:** في [`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs#L106-L117)، تُغلق النوافذ والحوارات المفتوحة بترتيب الأولوية من الأعلى للأسفل مع التحقق من حالة الخمول (`IsUpdateIdle`، `IsAccountIdle`، `IsScannerToolsEnabled`) لمنع إغلاق حوار أثناء عملية حرجة بلا إلغاء صريح.

---

## 4. السحب والإفلات (`DragDrop` & `DataTransfer` في Avalonia 11.3)

### استقبال الملفات والبيانات (`Drop Target`)
1. فعّل `DragDrop.AllowDrop="True"` في XAML على `MainWindow` أو الهدف المحدد.
2. سجّل معالجات `DragDrop.DragOverEvent` و`DragDrop.DropEvent` عبر `AddHandler(...)`.
3. في `DragOver`، افحص الصيغ المتاحة في `e.DataTransfer.Formats` واضبط `e.DragEffects = DragDropEffects.Copy` أو `DragDropEffects.None` لمنح مؤشر النظام تغذية راجعة دقيقة.
4. في `Drop`، اقرأ الملفات عبر `IStorageItem` (`TryGetFiles()`) أو النصوص عبر `TryGetText()`، وتحقق من وجود مسار محلي سليم وصيغة مدعومة قبل تمريره لطبقة `Application`.

### بدء عملية سحب (`Drag Source`)
```csharp
var data = new DataTransfer();
// عبّئ البيانات المطلوبة داخل DataTransfer...
DragDropEffects result = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
```
- **تنبيه:** لا تخلط بين السحب الداخلي لعناصر الكانفاس (الذي يعمل عبر `PointerPressed/Moved` والتقاط المؤشر لسرعة `60fps`) وبين `DragDrop.DoDragDropAsync` المخصص لنقل الحمولات عبر النظام أو بين عناصر واجهة منفصلة.
