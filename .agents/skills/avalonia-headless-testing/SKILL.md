---
name: avalonia-headless-testing
description: >-
  تغطي التوثيق المرجعي لاختبارات واجهة المستخدم دون شاشة عبر `Avalonia.Headless.XUnit` في Avalonia 11.3: تهيئة `TestAppBuilder` و`UseHeadlessDrawing`، سمات `[AvaloniaFact]`/`[AvaloniaTheory]`، تصفية طابور التخطيط (`UpdateLayout` و`Dispatcher.UIThread.RunJobs`)، محاكاة الماوس ولوحة المفاتيح (`MouseDown`, `KeyPressQwerty`, `KeyTextInput`)، فحص حدود RTL والثيم والوصولية، والتقاط الإطارات (`CaptureRenderedFrame`). تُفعّل عند كتابة أو تصحيح اختبارات `Ziwa.App.Tests`.
---

# مهارة اختبارات الواجهة الصامتة في Avalonia 11.3 (`avalonia-headless-testing`)

المراجع الحية في المشروع: [`TestAppBuilder.cs`](../../../tests/Ziwa.App.Tests/TestAppBuilder.cs) · [`WorkspaceChromeHeadlessTests.cs`](../../../tests/Ziwa.App.Tests/WorkspaceChromeHeadlessTests.cs) · [`Ziwa.App.Tests.csproj`](../../../tests/Ziwa.App.Tests/Ziwa.App.Tests.csproj).

---

## 1. التهيئة المعتمدة في `Ziwa.App.Tests`

يستخدم مشروع الاختبارات سمة التجميع `[assembly: AvaloniaTestApplication(typeof(Ziwa.App.Tests.TestAppBuilder))]` مع تفعيل Skia الحقيقي وإيقاف الرسم الوهمي:
```csharp
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions
    {
        UseHeadlessDrawing = false,
    });
```
- `UseHeadlessDrawing = false` + `.UseSkia()` يسمحان باختبار `SkiaViewportHost` و`CaptureRenderedFrame()` فعلياً؛ بينما `true` مناسبة فقط لمشاريع اختبارات التخطيط المجردة بلا Skia.

---

## 2. تشريح الاختبار القياسي (`[AvaloniaFact]` / `[AvaloniaTheory]`)

1. **استخدم `[AvaloniaFact]` أو `[AvaloniaTheory]`** (وليس `[Fact]`) لتشغيل الاختبار داخل حلقة المرسل (`Dispatcher`) الخاصة بـ Headless.
2. **افصل الموارد وحافظ على العزل داخل `try/finally`:**
   - احفظ `ThemeVariant? previousTheme = app.RequestedThemeVariant;` وأعده في `finally`.
   - أغلق النافذة (`window.Close();`) وتخلّص من `ServiceProvider` (`using ServiceProvider services = ...`) في كل اختبار حتى لا تتسرب حالة أو ثيم لاختبار لاحق.
3. **صفِّ التخطيط والمهام دائماً قبل الفحص (`Layout`):**
   - استدعِ `window.Show();` ثم `window.UpdateLayout();` و`Dispatcher.UIThread.RunJobs();` بعد أي تغيير في حجم النافذة أو خصائص `ViewModel` قبل قراءة `Bounds`.

---

## 3. ورقة غش محاكاة الإدخال وفحص الشجرة المرئية

| الهدف | الطريقة القياسية في `Avalonia.Headless` |
|---|---|
| النقر في منتصف زر/عنصر | احسب المركز عبر `control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)` ثم `window.MouseDown(..., MouseButton.Left)` + `MouseUp` |
| السحب والعجلة | `window.MouseMove(...)` و`window.MouseWheel(point, delta)` |
| ضغط مفتاح/اختصار مستقل عن تخطيط الكيبورد | `window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)` أو مع `RawInputModifiers.Control` |
| كتابة نص داخل `TextBox` مركّز | `window.KeyTextInput("نص عربي")` |
| فحص لقطة الشاشة المرسومة | `using WriteableBitmap? frame = window.CaptureRenderedFrame();` (**`using` إلزامي** لمنع تسرب ذاكرة Skia الأصلية) |

### مصفوفة التحقق المعتمدة للواجهات في Ziwa
- اختبر كل شاشة رئيسية عند المقاسين **`960×640`** (الحد الأدنى) و**`1360×860`** وفي الثيمين **`Dark`** و**`Light`** مع نصوص عربية طويلة.
- تحقّق هندسياً من: `FlowDirection.RightToLeft`، عدم تداخل الأزرار أو خروجها عن حدود شريطها (`AssertInside`)، بقاء جزيرة الكانفاس `LeftToRight` بينما تبقى القوائم والـ `QuickBar` بصيغة `RightToLeft`، ووجود `ToolTip.Tip` و`AutomationProperties.Name`.

---

## 4. أوامر التشغيل والفحص المستهدف

```powershell
dotnet test tests/Ziwa.App.Tests/Ziwa.App.Tests.csproj --filter "FullyQualifiedName~WorkspaceChromeHeadlessTests"
node scripts/avalonia-audit.mjs
```

---

## 5. التحقق الآلي والمراجع التفصيلية المتدرجة (`references/`)

- يفحص [`scripts/avalonia-audit.mjs`](../../../scripts/avalonia-audit.mjs) آلياً تصريف `CaptureRenderedFrame()` وعزل `RequestedThemeVariant` واستخدام `[AvaloniaFact]`/`[AvaloniaTheory]`.
- [أنماط ومزالق اختبارات Headless XUnit](references/headless-xunit-patterns-and-pitfalls.md): تشريح `RunJobs()`، حساب الإحداثيات في اختبارات الماوس، فحص تداخل RTL و`ScrollViewer`، تفادي سباقات `Task.Delay`، وتصريف `CaptureRenderedFrame()`.
