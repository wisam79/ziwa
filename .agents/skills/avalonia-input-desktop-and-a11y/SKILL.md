---
name: avalonia-input-desktop-and-a11y
description: >-
  تغطي التوثيق المرجعي لـ Avalonia UI 11.3 في الأحداث الموجهة (`Tunnel`/`Bubble`)، التقاط المؤشر (`Pointer.Capture` و`PointerCaptureLost`)، السحب والإفلات (`DragDrop` و`DataTransfer`)، خدمات `TopLevel` (`IStorageProvider` و`IClipboard` و`WindowNotificationManager`)، إطار النافذة المخصص (`ExtendClientAreaToDecorationsHint`)، النوافذ المنبثقة (`StudioDialogShell`، `Flyout`، `Popup`، `ContextMenu`، `AdornerLayer`)، والوصولية (`AutomationProperties` و`AutomationPeer`). تُفعّل عند تعديل مدخلات الماوس ولوحة المفاتيح والاختصارات، منتقي الملفات، الحافظة، الحوارات، أو إمكانية الوصول في `Ziwa.App`.
---

# مهارة المدخلات وخدمات سطح المكتب والوصولية في Avalonia 11.3 (`avalonia-input-desktop-and-a11y`)

المراجع الحية في المشروع: [`MainWindow.axaml.cs`](../../../src/Ziwa.App/Views/MainWindow.axaml.cs) · [`KeyboardCommandRouter.cs`](../../../src/Ziwa.App/Services/KeyboardCommandRouter.cs) · [`AvaloniaProjectDialogs.cs`](../../../src/Ziwa.App/Services/AvaloniaProjectDialogs.cs).

---

## 1. قواعد الإدخال الموجهة والتقاط المؤشر (`Input & Capture`)

1. **توجيه الأحداث (`Tunnel` مقابل `Bubble`):**
   - استعمل `AddHandler(..., RoutingStrategies.Tunnel)` فقط عندما تحتاج لاعتراض مفتاح أو إفلات قبل أن يستهلكه عنصر داخلي (مثل اعتراض `Enter` و`Esc` في `InlineCanvasTextBox` داخل [`MainWindow.axaml.cs`](../../../src/Ziwa.App/Views/MainWindow.axaml.cs)).
   - قبل تنفيذ أي اختصار عام في النافذة، تحقّق من عدم وجود حقل نصي مركز (`e.Source is TextBox || visual.FindAncestorOfType<TextBox>() is not null`) ولا تعرّف اختصارات متفرقة خارج [`KeyboardCommandRouter.cs`](../../../src/Ziwa.App/Services/KeyboardCommandRouter.cs) و`CommandRegistry`.
2. **القاعدة الذهبية لالتقاط المؤشر (`Pointer Capture`):**
   - أي عملية سحب أو تحجيم أو تحريك تبدأ بـ `e.Pointer.Capture(this)` **يجب** أن تقابلها إعادة تعيين نظيفة للحالة داخل `OnPointerCaptureLost` (وليس في `OnPointerReleased` وحدها)، وإلا تجمدت حالة السحب عند مقاطعة النظام أو تبديل النافذة.
3. **السحب والإفلات (`DragDrop` & `DataTransfer`):**
   - فعّل `DragDrop.AllowDrop="True"` على الهدف واضبط `e.DragEffects` بدقة داخل `DragOver` قبل قراءة الملفات أو النصوص في `Drop` مع فحص الامتدادات والأمان.

---

## 2. خدمات سطح المكتب عبر `TopLevel`

احصل على `TopLevel` عبر `TopLevel.GetTopLevel(control)` (أو `Window` المالك في خدمات الحوارات):
- **منتقي الملفات (`IStorageProvider`):** تحقّق من `CanOpen` / `CanSave`؛ استخدم `TryGetLocalPath()` أو `OpenReadAsync()` ولا تفترض أن المنتقي يُرجع مساراً محلياً دون فحص `null`.
- **الحافظة (`IClipboard`):** جميع العمليات غير متزامنة (`SetTextAsync`, `GetTextAsync`)؛ في Ziwa تخضع أي حمولة ملصوقة من الحافظة لفحص المخطط والأرقام المنتهية الموجبة قبل حقنها في المستند.
- **الإشعارات (`WindowNotificationManager`):** للإشعارات غير الحاجزة بمدة محددة (`ToastNotificationManager`).

---

## 3. مصفوفة اختيار الألواح العائمة والحوارات (`Overlays`)

| الأداة | متى تستخدمها في `Ziwa.App`؟ | سلوك الإغلاق والاتجاه |
|---|---|---|
| `StudioDialogShell` | النوافذ الحوارية داخل مساحة العمل (الطباعة، استوديو الملصقات، الماسح، الحساب) | يدعم `UseScrim` والنقر على التعتيم + `Esc`؛ `FlowDirection="RightToLeft"` مدمج |
| `Window.ShowDialog<T>(owner)` | تأكيدات نظامية عاجلة (مثل `ConfirmUnsavedChangesAsync` عند إغلاق التطبيق) | اربط `CancellationToken` بـ `Dispatcher.UIThread.Post(() => dialog.Close(...))` |
| `Flyout` / `MenuFlyout` | خيارات سريعة مرتبطة بزر (`المزيد`، قوائم القوالب) | إغلاق تلقائي عند النقر خارجه (`Light-dismiss`) |
| `ContextMenu` | قائمة السياق بالزر الأيمن على الكانفاس | أعد ضبط `FlowDirection="RightToLeft"` إذا كان المضيف داخل جزيرة `LeftToRight` |
| `Popup` / `AdornerLayer` | تراكبات دقيقة أو مقابض فوق العناصر دون تغيير شجرة التخطيط | حافظ على تنظيف الموضع عند تغير الحجم/التمرير |

---

## 4. ثوابت الكروم المخصص والوصولية (`Chrome & A11y`)

- عند تفعيل `ExtendClientAreaToDecorationsHint="True"` مع `NoChrome` في [`MainWindow.axaml`](../../../src/Ziwa.App/Views/MainWindow.axaml)، طبّق تعويض `OffScreenMargin` عند `WindowState == WindowState.Maximized` حتى لا تُقصّ حواف النافذة على ويندوز.
- كل عنصر تفاعلي يحمل `AutomationProperties.Name` و`ToolTip.Tip` بالعربية، ويدعم التنقل بلوحة المفاتيح ومؤشر التركيز `:focus-visible`.

---

## 5. التحقق الآلي والمراجع التفصيلية المتدرجة (`references/`)

- فحص قواعد الكروم المخصص واتجاه `ContextMenu` والوصولية والتقاط المؤشر آلياً: `node scripts/avalonia-audit.mjs` عبر [`scripts/avalonia-audit.mjs`](../../../scripts/avalonia-audit.mjs).
- [الأحداث الموجهة والتقاط المؤشر والسحب والإفلات](references/input-events-capture-and-dragdrop.md): `Tunnel`/`Bubble`، `PointerCaptureLost`، حماية مدخلات النص، سلّم `Esc`، و`IDataTransfer`.
- [خدمات TopLevel والكروم والألواح والوصولية](references/desktop-services-chrome-overlays-a11y.md): `IStorageProvider`، `IClipboard`، `ExtendClientArea*` + `OffScreenMargin`، مقارنة الألواح العائمة، و`AutomationPeer`.
