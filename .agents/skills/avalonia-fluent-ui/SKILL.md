---
name: avalonia-fluent-ui
description: نظام واجهة Avalonia UI 11 مع Fluent 2 RTL والتوكنز والربط المصرّف ودمج لوحة الرسم SkiaViewportHost
---

# بناء واجهات Fluent 2 RTL في Avalonia UI

المرجع التنفيذي: [`docs/DESIGN_SYSTEM.md`](../../../docs/DESIGN_SYSTEM.md).

---

## 1. إعداد مشروع الواجهة الصارم (`Ziwa.App.csproj`)
- فعّل:
  - `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`
  - `<Nullable>enable</Nullable>` و`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
- عيّن `FlowDirection="RightToLeft"` على الجذر (`MainWindow` / `DialogWindowBase`). استعمل `LeftToRight` فقط داخل حقول الكود اللوني `#RRGGBB` أو معادلات الأبعاد.

---

## 2. قاموس التوكنز (`Themes/Tokens.axaml`)
- **لا ألوان ثابتة في صفحات العرض:** كل لون يُستدعى عبر `{DynamicResource ...}` مع عكس دقيق في قواميس الثيم الفاتح والداكن (`ThemeDictionaries`).
- **سلم الاستدارة الموحّد:**
  - أزرار وحقول وعناصر داخلية: `4px` (`ControlCornerRadius`)
  - القوائم المنبثقة ولوحة الأوامر السريعة: `6px` (`OverlayCornerRadius`)
  - نوافذ الحوار (`Dialog`): `10px` (`DialogCornerRadius`)
- **محاذاة المساطر والألواح:** مساطر الكانفاس بسُمك `20px` مثبتة في حواف الـ Viewport خارج التمرير، مع تمييز نقطة `(0,0)` وحدود الورقة الفعلية.

---

## 3. ربط لوحة الرسم `SkiaViewportHost` بأعلى سلاسة (`60–120fps`)
- لا تستخدم `WriteableBitmap` مع نسخ بايتات على الـ CPU في كل إطار!
- في **Avalonia 11**، أنشئ تحكماً مخصصاً يطبق `Render(DrawingContext context)` ويمرر عملية رسم `ICustomDrawOperation`:
  1. داخل `ICustomDrawOperation.Render(ImmediateDrawingContext context)`: اطلب `ISkiaSharpApiLeaseFeature` via `context.TryGetFeature<ISkiaSharpApiLeaseFeature>()`.
  2. استأجر `using var lease = leaseFeature.Lease();` واحصل على `var canvas = lease.SkCanvas;` — هذا يرسم مباشرة على سطح الـ GPU الخاص بـ Avalonia!
  3. مرّر `canvas` ومعاملات الـ Viewport (`Zoom`, `PanX`, `PanY`, `DpiScale`) إلى `SceneRenderer.RenderPage(...)`.
  4. أبقِ `SkiaViewportHost` نقياً من منطق الأعمال: يستقبل مدخلات الماوس/اللمس (`PointerPressed/Moved/Wheel`) ويحوّل الإحداثيات إلى فضاء المليمتر عبر `ViewportTransform` ثم يطلق أوامر ViewModel.

---

## 4. انضباط UI Hygiene وتجربة المستخدم
- **أزرار الأيقونات:** تحتوي دائماً على `ToolTip.Tip` و`AutomationProperties.Name` وصفيين، وبهدف نقر لا يقل عن `28×28px` للمحرر أو `44×44px` للمس.
- **منزلقات الخصائص وحالات الانتظار:** كل منزلق (`FluentSliderRow`) يعرض الوحدة والـ Default ويدعم الإعادة بنقرة مزدوجة، ولا يُنشئ `Undo` أثناء السحب المستمر بل عند الإفلات فقط (`PointerCaptureLost`/`Commit`).
- **الألوان المتدرجة والحوارات:** نوافذ الحوار تستخدم أزرار إغلاق متسقة، وقبل إلغاء أمر طويل (`AI` / `Export`) يجب توفير زر إلغاء مربوط بـ `CancellationToken`.
