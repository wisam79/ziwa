# أنماط ومزالق اختبارات `Avalonia.Headless.XUnit` في `Ziwa.App.Tests`

> مرجع عملي مستخلص من توثيق **Avalonia 11.3 Headless** وتجربة الاختبارات الفعلية في [`WorkspaceChromeHeadlessTests.cs`](../../../../tests/Ziwa.App.Tests/WorkspaceChromeHeadlessTests.cs) و[`TestAppBuilder.cs`](../../../../tests/Ziwa.App.Tests/TestAppBuilder.cs).

---

## 1. توقيت التخطيط و`Dispatcher.UIThread.RunJobs()`

عمليات القياس والترتيب وتبديل القوالب وتمرير `ScrollViewer` في Avalonia غير متزامنة؛ قراءة `Bounds` أو `Extent` مباشرة بعد `window.Show()` أو تغيير خاصية في `ViewModel` قبل تفريغ المرسل تعطي قيماً صفرية أو قديمة.

```csharp
private static void Layout(Window window)
{
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
}
```
- **متى تستدعي `Layout(window)`؟**
  1. فور `window.Show()`.
  2. بعد تغيير `window.Width`/`Height`، أو تبديل `vm.IsDarkTheme`، أو تعديل `scroller.Offset`.
  3. بعد تحفيز أمر يفتح أو يغلق لوحاً (`InspectorPanelView`، `QuickBar`، أو حوار `StudioDialogShell`).

---

## 2. محاكاة الإدخال الدقيقة (الماوس ولوحة المفاتيح)

### أ. النقر الآمن على عنصر تحكم (`Click`)
لا تعتمد على إحداثيات صلبة مخمّنة؛ احسب موضع العنصر الفعلي نسبةً إلى `Window`:
```csharp
private static void Click(Window window, Control target)
{
    Point? center = target.TranslatePoint(
        new Point(target.Bounds.Width / 2, target.Bounds.Height / 2),
        window);
    Assert.NotNull(center);
    window.MouseDown(center.Value, MouseButton.Left, RawInputModifiers.None);
    window.MouseUp(center.Value, MouseButton.Left, RawInputModifiers.None);
    Layout(window);
}
```
- **انتبه لاختبارات `SkiaViewportHost`:** تأكد أولاً أن العنصر مرئي (`IsVisible == true` و`IsEffectivelyVisible == true`) وأن أبعاده غير صفرية (`Bounds.Width > 0`)، وأن الأيقونة الداخلية لا تحجب الاختبار بفضل `IsHitTestVisible="False"`.

### ب. مفاتيح لوحة المفاتيح (`KeyPressQwerty` مقابل `KeyTextInput`)
- استعمل `window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)` للاختصارات ومفاتيح التحكم (`Enter`, `Escape`, `Delete`, `Ctrl+K`, `Ctrl+Z`) لأنها مستقلة عن لغة تخطيط لوحة المفاتيح في نظام التشغيل.
- استعمل `window.KeyTextInput("...")` لمحاكاة إدخال نصوص حقيقية في `TextBox` مركّز (`control.Focus()`).

---

## 3. فحوصات التطابق البصري والهندسي لـ RTL والوصولية

من [`WorkspaceChromeHeadlessTests.cs`](../../../../tests/Ziwa.App.Tests/WorkspaceChromeHeadlessTests.cs) في [`MainWindow.axaml`](../../../../src/Ziwa.App/Views/MainWindow.axaml):
1. **ترتيب الأعمدة في `RightToLeft`:**
   - سكة الأدوات يمين الكانفاس: `BoundsIn(rail, window).Left >= BoundsIn(workspace, window).Right`.
   - لوح المفتش يسار الكانفاس: `BoundsIn(inspector, window).Right <= BoundsIn(workspace, window).Left`.
2. **احتواء العناصر وعدم القص (`AssertInside`):**
   - تحقّق لكل زر في شريط الأدوات وشريط الصفحات أن مستطيل حدوده داخل حدود الشريط الحاوي وأنه لا يتقاطع مع شقيق مجاور (`!boundsA.Intersects(boundsB)`).
3. **اتساق السمة والوصولية:**
   - `Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);`
   - تحقّق من `ToolTip.GetTip(...)` و`AutomationProperties.GetName(...)` خاصةً للأزرار الأيقونية والعناوين المقصوصة بـ `CharacterEllipsis`.
4. **ثبات إجراءات شريط الصفحات أثناء التمرير:**
   - مرّر `ScrollViewer` للألسنة إلى أقصى مدى (`scroller.Extent.Width - scroller.Viewport.Width`) ثم تأكد أن حدود `PageActions` لم تتغير ولم تتداخل مع الألسنة.

---

## 4. التقاط الإطارات المرسومة (`CaptureRenderedFrame`) وإدارة الذاكرة

عند الحاجة لفحص البكسلات المرسومة فعلياً (مفعل بفضل `UseHeadlessDrawing = false` و`.UseSkia()`):
```csharp
using var frame = window.CaptureRenderedFrame();
Assert.NotNull(frame);
```
- **تحذير تسرب ذاكرة:** تُرجع `CaptureRenderedFrame()` كائن `WriteableBitmap` مدعوماً بذاكرة أصلية؛ غلّفه دائماً بـ `using` أو استدعِ `Dispose()`.

---

## 5. قائمة فحص لمنع الاختبارات المتذبذبة (`Flaky Tests`)

- **لا تستخدم `Task.Delay` للانتظار العشوائي:** استعمل `Dispatcher.UIThread.RunJobs()` أو انتظر تحقق حالة حتمية من الـ `ViewModel`.
- **اعزل كل اختبار:** أعِد `Application.Current!.RequestedThemeVariant = previousTheme;` وأغلق النافذة `window.Close();` وتخلّص من حاوية DI داخل `finally`.
- **لا تنسَ فصل الاختبارات الثقيلة:** شغّل الاختبارات المستهدفة محلياً بـ `--filter` واترك تشغيل الحزمة الكاملة لخط `CI`.
