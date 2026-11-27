# التنسيق والسمات واستعلامات الحاويات في Avalonia UI 11.3

> مرجع تطبيقي مبني على توثيق **Avalonia 11.3** وبنية الثيم في [`App.axaml`](../../../../src/Ziwa.App/App.axaml) و[`Tokens.axaml`](../../../../src/Ziwa.App/Themes/Tokens.axaml).

---

## 1. `ControlTheme` مقابل `Style`: التصميم المعتمد في Avalonia 11

على عكس WPF، يفصل Avalonia 11 بين **قالب العنصر** (`ControlTheme`) و**قواعد التخصيص** (`Style`):

1. **`ControlTheme` (في `ResourceDictionary` عبر `ResourceInclude`):**
   - يُخزّن بمفتاح `x:Key="{x:Type controls:StudioCard}"` أو مفتاح مسمّى، ويُطبّق تلقائياً على جميع مثيلات النوع أو عبر `Theme="{StaticResource ...}"`.
   - يدعم الوراثة عبر `BasedOn="{StaticResource {x:Type Button}}"` لتوريث القالب والخصائص ثم تعديل الفروق فقط.
   - يستخدم المحدد النسبي `^` داخل الأنماط المتداخلة للإشارة إلى العنصر المضيف نفسه:
     ```xml
     <ControlTheme x:Key="{x:Type controls:StudioSliderRow}" TargetType="controls:StudioSliderRow">
       <Setter Property="Template">
         <ControlTemplate TargetType="controls:StudioSliderRow">
           <Border x:Name="PART_ResetArea" Background="{DynamicResource ZiwaSurfaceBrush}">
             <Slider x:Name="PART_Slider"
                     Minimum="{TemplateBinding Minimum}"
                     Maximum="{TemplateBinding Maximum}"
                     Value="{TemplateBinding Value, Mode=TwoWay}" />
           </Border>
         </ControlTemplate>
       </Setter>

       <Style Selector="^:pointerover /template/ Border#PART_ResetArea">
         <Setter Property="BorderBrush" Value="{DynamicResource ZiwaSecondaryTextBrush}" />
       </Style>
     </ControlTheme>
     ```
2. **`Style` (في `Styles` عبر `StyleInclude`):**
   - تُجمّع فئات العمل (`Classes="workspaceAction primary"`) والتعديلات المشتركة على العناصر الجاهزة في [`WorkspaceStyles.axaml`](../../../../src/Ziwa.App/Themes/WorkspaceStyles.axaml) و[`AppleHigStyles.axaml`](../../../../src/Ziwa.App/Themes/AppleHigStyles.axaml).

---

## 2. أولوية المحددات وحالات التفاعل (`Selector Precedence`)

ترتيب حسم القيم في Avalonia 11 (من الأعلى للأدنى):
1. القيم المحلية المباشرة على العنصر في XAML أو الكود (`LocalValue`).
2. الحركات والانتقالات الجارية (`Animation` / `Transitions`).
3. الأنماط ذات المحددات الشرطية أو الحالات الزائفة (`:pointerover`، `:pressed`، `:focus-visible`، `:disabled`، `[IsOpen=True]`).
4. الأنماط الثابتة (تُرجّح الأكثر تحديداً مثل `Button.workspaceAction.primary`، ثم الأحدث في الترتيب).
5. `ControlTheme` الأساسي للعنصر، ثم الوراثة والقيمة الافتراضية للخاصية.

### لماذا يختفي أثر `Button:pointerover` أحياناً؟
قالب `FluentTheme` للزر يضع خلفية وحالة المؤشر على `ContentPresenter#PART_ContentPresenter` داخل القالب، لذلك إذا كتبت `Button.myBtn:pointerover` على مستوى `Button` فقط فقد يظل لون الـ `PART_ContentPresenter` مهيمناً في حالات التمرير والضغط.
- **الحل المعتمد في Ziwa:** استهدف جزء القالب مباشرة كما في [`WorkspaceStyles.axaml`](../../../../src/Ziwa.App/Themes/WorkspaceStyles.axaml):
  ```xml
  <Style Selector="Button.workspaceAction:not(.primary):pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource ZiwaSubtleFillBrush}" />
  </Style>
  <Style Selector="Button.workspaceAction:focus-visible /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="BorderBrush" Value="{DynamicResource ZiwaFocusStrokeBrush}" />
    <Setter Property="BorderThickness" Value="2" />
  </Style>
  ```

---

## 3. `ThemeDictionaries` و`ThemeVariantScope` وإلزامية `DynamicResource`

- تُعرّف الألوان والفرش في [`Tokens.axaml`](../../../../src/Ziwa.App/Themes/Tokens.axaml) داخل `<ResourceDictionary.ThemeDictionaries>` بمفتاحين: `x:Key="Light"` و`x:Key="Dark"`.
- **قاعدة الربط الحرجة:**
  - استعمل `{DynamicResource ...}` دائماً لأي لون أو فرشاة تتغير بتبديل الثيم (`ZiwaSurfaceBrush`, `ZiwaCardBrush`, `ZiwaPrimaryTextBrush`). استخدام `{StaticResource}` مع فرشاة ثيم يُجمّدها على الثيم الابتدائي عند الإقلاع ولا يتجاوب مع تبديل الوضع الفاتح/الداكن.
  - استعمل `{StaticResource ...}` للقيم البنيوية الثابتة غير المرتبطة بالثيم (`ControlCornerRadius`, `HairlineBorderThickness`, `ToolbarButtonHeight`, `Icon*`).
- **عزل فرع من الواجهة (`ThemeVariantScope`):** عند الحاجة لعرض معاينة فاتحة داخل تطبيق داكن (أو العكس)، غلّف الفرع بـ `<ThemeVariantScope RequestedThemeVariant="Light">...</ThemeVariantScope>` لتقرأ كل `{DynamicResource}` داخله من قاموس `Light` تلقائياً.

---

## 4. استعلامات الحاويات في Avalonia 11.3 (`ContainerQuery`)

تسمح `ContainerQuery` بتغيير أنماط العناصر استناداً إلى أبعاد حاوية مسمّاة بدل حجم النافذة كله (مثال حي في [`MainWindow.axaml`](../../../../src/Ziwa.App/Views/MainWindow.axaml#L37-L38)):

```xml
<!-- 1. وسم الحاوية المراقَبة -->
<Grid x:Name="CanvasWorkspaceRegion"
      Container.Name="QuickBarViewport"
      Container.Sizing="Width">
  <!-- الأبناء المستجيبون -->
</Grid>
```

```xml
<!-- 2. استعلام التكيّف داخل <Styles> -->
<ContainerQuery Name="QuickBarViewport" Query="max-width:620">
  <Style Selector="TextBlock.quickBarCaption">
    <Setter Property="IsVisible" Value="False" />
  </Style>
</ContainerQuery>
```

- **قيد رسمي مهم:** لا يمكن لـ `ContainerQuery` أن يستهدف العنصر المعلّم بـ `Container.Name` نفسه؛ يطبّق أسلوبه على العناصر **الأبناء** فقط لتجنب حلقات القياس اللانهائية.
- قيم `Container.Sizing`: `Width`، `Height`، `WidthAndHeight` (فضّل `Width` أو `Height` وحدها متى أمكن لتقليل إعادة التقييم).

---

## 5. مرجع التوكنز الفعلي في `Ziwa.App` (`Tokens.axaml`)

| الفئة | المفاتيح المعتمدة في [`Tokens.axaml`](../../../../src/Ziwa.App/Themes/Tokens.axaml) | الملاحظات |
|---|---|---|
| الأسطح (`Brushes`) | `ZiwaCanvasBackgroundBrush`, `ZiwaSurfaceBrush`, `ZiwaCardBrush`, `ZiwaElevatedCardBrush`, `ZiwaSubtleFillBrush`, `ZiwaHoverFillBrush`, `ZiwaPressedFillBrush`, `ZiwaRulerBackgroundBrush`, `ZiwaScrimBrush`, `ZiwaQrSurfaceBrush` | متوفرة في `Light` و`Dark` عبر `DynamicResource` |
| الإجراءات والحالات | `ZiwaPrimaryActionBrush`, `ZiwaPrimaryHoverBrush`, `ZiwaPrimaryPressedBrush`, `ZiwaPrimarySubtleBrush`, `ZiwaDestructiveActionBrush`, `ZiwaDestructiveSubtleBrush`, `ZiwaSuccessBrush`, `ZiwaWarningBrush`, `ZiwaInfoBrush` | تباين نصي `>= 4.5:1` مع `ZiwaOnPrimaryActionBrush` |
| النصوص والحدود | `ZiwaPrimaryTextBrush`, `ZiwaSecondaryTextBrush`, `ZiwaHairlineBorderBrush`, `ZiwaControlBorderBrush`, `ZiwaFocusStrokeBrush` | `HairlineBorderThickness = 1` |
| الاستدارة (`CornerRadius`) | `CheckBoxCornerRadius` (`3`), `ControlCornerRadius` (`6`), `OverlayCornerRadius` (`10`), `DialogCornerRadius` (`12`), `PillCornerRadius` (`999`) | تتبع سلم Apple HIG |
| الأبعاد والحواشي | `ToolbarButtonHeight` (`30`), `CompactButtonSize` (`28`), `RailWidth` (`60`), `InspectorWidth` (`292`), `RulerThickness` (`20`), `InspectorPadding` (`14`), `CardPadding` (`12`), `ControlPadding` (`10,5`) | متطابقة مع `GridLength` المقابلة |
| الظلال (`BoxShadows`) | `ZiwaCardShadow`, `ZiwaFloatingShadow`, `ZiwaSheetShadow` | تُستدعى بـ `StaticResource` على `Border.BoxShadow` |
