---
name: avalonia-fluent-ui
description: >-
  تغطي التوثيق المرجعي لـ Avalonia UI 11.3 في التنسيق (`Style` مقابل `ControlTheme`)، قواميس السمات (`ThemeDictionaries` و`ThemeVariantScope`)، استعلامات الحاويات (`ContainerQuery` و`Container.Name`)، الربط المصرّف (`CompiledBindings`)، القوالب (`DataTemplates`)، تخطيط RTL والخطوط العربية المضمّنة، وتوكنز Apple HIG في `Ziwa.App`. تُفعّل عند تصميم أو تعديل ملفات `.axaml`، الأنماط، الثيم الفاتح/الداكن، قوالب العرض، أو تخطيطات RTL في `Ziwa.App`.
---

# مهارة التنسيق والسمات والربط المصرّف في Avalonia UI 11.3 (`avalonia-fluent-ui`)

المرجع التصميمي لـ Ziwa: [`docs/DESIGN_SYSTEM.md`](../../../docs/DESIGN_SYSTEM.md) · التوكنز: [`Tokens.axaml`](../../../src/Ziwa.App/Themes/Tokens.axaml) · قوالب الاستوديو: [`StudioControlThemes.axaml`](../../../src/Ziwa.App/Themes/StudioControlThemes.axaml).

---

## 1. مصفوفة القرار: أين أضع التنسيق والموارد؟

| الآلية | الحاوية الصحيحة | الاستيراد في `App.axaml` | متى تُستخدم؟ |
|---|---|---|---|
| `ControlTheme` | `ResourceDictionary` | `ResourceInclude` | تعريف الهيكل البصري (`ControlTemplate`) والحالات الأساسية لعنصر `TemplatedControl` مثل `StudioDialogShell` و`StudioCard` و`StudioSliderRow` |
| `Style` + `Selector` | `Styles` | `StyleInclude` | تخصيص فئات (`Classes="workspaceAction"`)، استهداف حالات مثل `:pointerover` و`:focus-visible`، أو قواعد استجابة متفرعة |
| `ThemeDictionaries` | `ResourceDictionary` | `ResourceInclude` | تعريف `Color` و`SolidColorBrush` لمتغيرات `Light` و`Dark` مع استدعائها بـ `{DynamicResource ...}` حصراً |
| `ThemeVariantScope` | شجرة العرض (`XAML`) | — | عزل جزء من الواجهة بسمة مختلفة (`Light`/`Dark`) دون التأثير على بقية النافذة |
| `ContainerQuery` | `Styles` | — | تكييف شريط أو لوح (`QuickBarViewport`) مع عرض حاويته (`Container.Sizing="Width"`) بدل عرض النافذة الكلي |

---

## 2. ثوابت Avalonia 11.3 في `Ziwa.App`

1. **الربط المصرّف إجباري (`CompiledBindings`):**
   - مفعّل افتراضياً في [`Ziwa.App.csproj`](../../../src/Ziwa.App/Ziwa.App.csproj) عبر `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`.
   - صرّح بـ `x:DataType="..."` على كل نافذة، `UserControl`، وكل `<DataTemplate>`.
   - في **Avalonia 11.3+** يستنتج المصرّف نوع `DataContext` تلقائياً في `#Element.DataContext.Prop` و`$parent[Window].DataContext.Prop` متى حُدّد `x:DataType` على العنصر المرجعي؛ تجنّب `ReflectionBinding` نهائياً.
2. **توكنز `Tokens.axaml` (Apple HIG RTL) بدون ألوان Hex خام:**
   - استعمل `{DynamicResource ...}` لجميع الأسطح والحدود والنصوص (`ZiwaSurfaceBrush`، `ZiwaCardBrush`، `ZiwaElevatedCardBrush`، `ZiwaHairlineBorderBrush`، `ZiwaPrimaryActionBrush`).
   - سلم الاستدارة المعتمد في `Tokens.axaml`: `ControlCornerRadius` (`6`)، `CheckBoxCornerRadius` (`3`)، `OverlayCornerRadius` (`10`)، `DialogCornerRadius` (`12`)، `PillCornerRadius` (`999`).
   - أبعاد المحاور الثابتة: `RailWidth` (`60`)، `InspectorWidth` (`292`)، `RulerThickness` (`20`)، `ToolbarButtonHeight` (`30`)، `CompactButtonSize` (`28`).
3. **اتجاه RTL وجزر LTR المنضبطة:**
   - تضبط [`MainWindow.axaml`](../../../src/Ziwa.App/Views/MainWindow.axaml) الجذر بـ `FlowDirection="RightToLeft"`.
   - تُعزل منطقة الكانفاس والمساطر (`CanvasWorkspaceRegion`) بـ `FlowDirection="LeftToRight"` لتطابق محاور الطباعة (`+X` يميناً، `+Y` أسفلاً)، مع إعادة `FlowDirection="RightToLeft"` صراحةً على `ContextMenu` و`ContextualQuickBar` و`InlineTextEditOverlay` والنوافذ المنبثقة.
   - الحقول التقنية (`#RRGGBB`، نسب التكبير مثل `100%`، وأبعاد `W × H`) تأخذ `FlowDirection="LeftToRight"`.
4. **مكونات الاستوديو القياسية والخطوط:**
   - استخدم مكونات Ziwa الموحدة: `StudioDialogShell`، `StudioCard`، `StudioNumberField`، `StudioSliderRow` ولا تكرر أنماط الأزرار والبطاقات؛ راجع [`WorkspaceStyles.axaml`](../../../src/Ziwa.App/Themes/WorkspaceStyles.axaml).
   - خط الواجهة الموروث هو `IBM Plex Sans Arabic` (`ContentControlThemeFontFamily` ← `ZiwaIbmPlexArabicFont`) ويبقى مستقلاً عن خطوط المستند والطباعة.
5. **انضباط الكثافة وتجربة الانتظار (`UI Hygiene` & `Wait UX`):**
   - هدف النقر لا يقل عن `28×28` في المحرر و`44×44` في سكة الأدوات واللمس؛ اختبر عند أصغر نافذة (`960×640`) مع فتح المفتش.
   - كل زر أيقوني يحمل `ToolTip.Tip` و`AutomationProperties.Name` بالعربية، و`IsHitTestVisible="False"` على `PathIcon` بداخله.
   - لا وميض انتظار لأقل من `1s`؛ فوق `3s` أو عمليات الشبكة أظهر حالة صادقة وزر إلغاء مربوطاً بـ `CancellationToken`.

---

## 3. محاذير شائعة يجب تجنبها

- **لا تستهدف `Button:pointerover` على المستوى السطحي مع `FluentTheme`:** في قوالب Fluent يغطي `ContentPresenter#PART_ContentPresenter` الخلفية؛ استهدف `Button.cls:pointerover /template/ ContentPresenter#PART_ContentPresenter` كما في `WorkspaceStyles.axaml`.
- **لا تضع `VirtualizingStackPanel` داخل `StackPanel`:** يحتاج حاوية مقيدة الطول (`Grid` بصف `*` أو `DockPanel`) وإلا تعطلت الافتراضية بصمت.
- **لا تطبّق `ContainerQuery` على الحاوية نفسها:** `ContainerQuery` يستهدف الأبناء داخل الحاوية المعلّمة بـ `Container.Name` فقط.

---

## 4. التحقق الآلي والمراجع التفصيلية المتدرجة (`references/`)

- فحص قواعد AXAML والسمات والربط آلياً: `node scripts/avalonia-audit.mjs` (أو `--strict`) عبر [`scripts/avalonia-audit.mjs`](../../../scripts/avalonia-audit.mjs).
- [التنسيق والسمات واستعلامات الحاويات](references/styling-themes-and-container-queries.md): `ControlTheme` و`BasedOn` وأولوية المحددات `^` و`ThemeDictionaries` و`ContainerQuery` وجدول توكنز `Tokens.axaml`.
- [الربط المصرّف والقوالب والافتراضية](references/compiled-bindings-and-templates.md): استنتاج `DataContext` في 11.3 و`TemplateBinding` و`IDataTemplate` و`FuncDataTemplate` و`VirtualizingStackPanel`.
- [التخطيط واتجاه RTL والخطوط المضمّنة](references/layout-rtl-and-typography.md): حساب القياس والترتيب، جزر `LeftToRight`/`RightToLeft`، والخطوط المضمّنة عبر `avares://` و`EmbeddedFontCollection`.
