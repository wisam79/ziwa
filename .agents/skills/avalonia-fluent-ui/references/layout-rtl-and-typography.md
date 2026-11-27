# التخطيط واتجاه RTL والخطوط العربية المضمّنة في Avalonia UI 11.3

> مرجع قواعد التخطيط ثنائي Pass، وعكس الاتجاه `RightToLeft`/`LeftToRight`، وتغليف الخطوط العربية في `Ziwa.App`.

---

## 1. نظام التخطيط وحساب المساحات (`Measure` & `Arrange`)

يعمل تخطيط Avalonia على مرحلتين: **القياس (`Measure`)** لحساب `DesiredSize` ثم **الترتيب (`Arrange`)** لتثبيت `Bounds`.

### قواعد التوزيع المعتمدة في `Ziwa.App`
- **أعطِ العنصر مساحة وظيفته:**
  - `Auto` للأزرار، العدّادات القصيرة، الأيقونات، والألواح ذات العرض الثابت مثل `WorkspaceRailView` (`RailGridLength = 60`) و`InspectorPanelView` (`InspectorGridLength = 292`).
  - `*` للمحتوى القابل للتمدد (`SkiaViewportHost`، شريط ألسنة الصفحات القابل للتمرير، قوائم البحث).
- **تجنّب قصّ أو تجاوز النصوص العربية في الأعمدة الضيقة:**
  - الحقول داخل المفتش (`InspectorWidth = 292px`) تحتاج `MinWidth="0"` (مطبّق عالمياً على `TextBox` و`ComboBox` في [`AppleHigStyles.axaml`](../../../../src/Ziwa.App/Themes/AppleHigStyles.axaml)) حتى لا تفرض عرضاً أدنى يدفع العناصر خارج العمود.
  - العناوين المتغيرة الطول (اسم المشروع، رسالة الحالة) تستخدم `TextTrimming="CharacterEllipsis"` مع `ToolTip.Tip="{Binding ...}"` لإتاحة النص الكامل عند التحويم (مختبَر في `WorkspaceChromeHeadlessTests`).
  - لا تستخدم `Width` ثابتاً لأزرار أو شارات نصية عربية؛ اتركها تتكيف مع محتواها مع `MinWidth` و`Padding` مناسبين.

---

## 2. انضباط `FlowDirection="RightToLeft"` وجزر `LeftToRight`

في Avalonia 11 تنتقل خاصية `FlowDirection` بالوراثة إلى الأبناء، وتعكس أفقياً ترتيب أعمدة `Grid`، واتجاه `DockPanel`، و`StackPanel` الأفقي، ومحاذاة `HorizontalAlignment`.

### المعمارية ثنائية الاتجاه في [`MainWindow.axaml`](../../../../src/Ziwa.App/Views/MainWindow.axaml)
1. **الجذر والكروم والألواح: `RightToLeft`**
   - تضبط `MainWindow` و`StudioDialogShell` الجذر بـ `FlowDirection="RightToLeft"`.
   - سكة الأدوات تظهر يميناً، والكانفاس في الوسط، والمفتش يساراً تلقائياً عبر `Grid.ColumnDefinitions="Auto,*,Auto"`.
2. **منطقة الكانفاس والمساطر: جزيرة `LeftToRight` محصورة**
   - لأن إحداثيات الطباعة والمليمتر ثابتة (`+X` يميناً، `+Y` أسفلاً)، تأخذ `CanvasWorkspaceRegion` خاصية `FlowDirection="LeftToRight"`.
   - **قاعدة الاستعادة الإلزامية:** أي عنصر واجهة بشري يعيش بصرياً *داخل* `CanvasWorkspaceRegion` (مثل `SkiaViewportHost.ContextMenu` و`ContextualQuickBar` و`InlineTextEditOverlay`) **يجب** أن يعيد ضبط `FlowDirection="RightToLeft"` على جذره؛ وإلا انعكست قوائمه وأزراره بالخطأ!
3. **حالات `LeftToRight` التقنية الصغرى:**
   - الأكواد اللونية (`#RRGGBB`)، نسب التكبير (`100%`)، أرقام الصفحات التقنية (`1 / 18`)، وحقول الأبعاد الرقمية `W × H` توضع في حاوية `FlowDirection="LeftToRight"` حتى لا تتبدل مواضع الإشارات والنسب مع النصوص العربية المجاورة.

---

## 3. الخطوط العربية الأوفلاين المضمّنة (`avares://`)

تُضمّن الخطوط العربية داخل حزمة التطبيق لضمان تطابق العرض عبر الأجهزة حتى دون تثبيت الخطوط على النظام:

1. **التسجيل في [`Ziwa.App.csproj`](../../../../src/Ziwa.App/Ziwa.App.csproj):**
   ```xml
   <ItemGroup>
     <AvaloniaResource Include="..\Ziwa.Rendering\Resources\Fonts\*.ttf"
                       Link="Assets\Fonts\%(Filename)%(Extension)" />
   </ItemGroup>
   ```
2. **التعريف في [`Tokens.axaml`](../../../../src/Ziwa.App/Themes/Tokens.axaml):**
   ```xml
   <FontFamily x:Key="ZiwaCairoFont">avares://Ziwa.App/Assets/Fonts#Cairo</FontFamily>
   <FontFamily x:Key="ZiwaTajawalFont">avares://Ziwa.App/Assets/Fonts#Tajawal</FontFamily>
   <FontFamily x:Key="ZiwaIbmPlexArabicFont">avares://Ziwa.App/Assets/Fonts#IBM Plex Sans Arabic</FontFamily>
   <StaticResource x:Key="ContentControlThemeFontFamily" ResourceKey="ZiwaIbmPlexArabicFont" />
   ```
   - `ContentControlThemeFontFamily` يجعل `FluentTheme` يورّث خط `IBM Plex Sans Arabic` تلقائياً إلى جميع النوافذ والأزرار والقوائم دون تكرار `FontFamily` في كل صفحة.
3. **التسجيل البرمجي الاختياري (`EmbeddedFontCollection` عبر `AppBuilder.ConfigureFonts`):**
   - متاح عند الحاجة لمجموعات خطوط مجمّعة ببادئة `fonts:ZiwaFonts#FamilyName` عبر `FontManager.AddFontCollection(...)`.
4. **فصل خط الواجهة عن خط التصميم:** لا تفرض خط واجهة التطبيق (`IBM Plex Sans Arabic`) على عقد النصوص داخل المستند (`TextNode`) أو مخرجات الطباعة؛ فخط التصميم يديره `FontRegistry` و`HarfBuzzArabicShaper` في `Ziwa.Rendering`.
