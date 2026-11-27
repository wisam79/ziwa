# الربط المصرّف والقوالب والافتراضية في Avalonia UI 11.3

> مرجع عملي لميزات **Avalonia 11.3** في `CompiledBindings` و`DataTemplates` و`VirtualizingStackPanel` داخل `Ziwa.App`.

---

## 1. الربط المصرّف (`CompiledBindings`) واستنتاج النوع في 11.3

تفعيل `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` في [`Ziwa.App.csproj`](../../../../src/Ziwa.App/Ziwa.App.csproj) يحوّل أخطاء أسماء الخصائص في XAML إلى أخطاء ترجمة (Build Errors) ويزيل الانعكاس (`Reflection`) وقت التشغيل.

### قواعد إلزامية في ملفات `.axaml`
1. عرّف `x:DataType="vm:YourViewModel"` على جذر كل `Window` أو `UserControl`.
2. عرّف `x:DataType="vm:ItemViewModel"` على كل `<DataTemplate>` حتى لو ورث العنصر الأب نوعاً آخر؛ لأن سياق العنصر داخل القالب يتبدل إلى عنصر القائمة.
3. **ميزة Avalonia 11.3 (استنتاج `DataContext` التلقائي):**
   - في الربط مع سياق أب أو عنصر مسمّى، لم يعد يلزم التصريح بالتحويل الصريح `((vm:MainWindowViewModel)DataContext)` متى كان `x:DataType` محدداً على ذلك الأصل:
   ```xml
   <Window x:Name="RootWindow" x:DataType="vm:MainWindowViewModel">
     <ItemsControl ItemsSource="{Binding Pages}">
       <ItemsControl.ItemTemplate>
         <DataTemplate x:DataType="vm:PageTabItemViewModel">
           <Button Content="{Binding Title}"
                   Command="{Binding #RootWindow.DataContext.SelectPageCommand}"
                   CommandParameter="{Binding Index}" />
         </DataTemplate>
       </ItemsControl.ItemTemplate>
     </ItemsControl>
   </Window>
   ```
   - الصيغة البديلة عبر الشجرة المرئية: `{Binding $parent[Window].DataContext.SelectPageCommand}`.
4. **متى يُسمح بتعطيل الربط المصرّف؟** يُمنع استخدام `ReflectionBinding` أو `x:CompileBindings="False"` في `Ziwa.App` إلا عند تعذّر تحديد النوع تقنياً مع توثيق السبب بتعليق صريح.

---

## 2. `TemplateBinding` مقابل `RelativeSource TemplatedParent`

داخل `<ControlTemplate>` في ملفات القوالب مثل [`StudioControlThemes.axaml`](../../../../src/Ziwa.App/Themes/StudioControlThemes.axaml):

| الآلية | الاتجاه الافتراضي | متى تستخدمها؟ |
|---|---|---|
| `{TemplateBinding Prop}` | `OneWay` غالباً (أسرع وأخف) | ربط خصائص العرض (`Text`, `Background`, `CornerRadius`, `Command`, `IsEnabled`) |
| `{TemplateBinding Value, Mode=TwoWay}` أو `{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}` | `TwoWay` + يدعم المحوّلات الكاملة | عندما يحتاج جزء القالب إلى تحديث خاصية المضيف ثنائياً (مثل `Slider.Value`) أو استخدام محوّل معقّد |

---

## 3. المحوّلات الجاهزة والربط المتعدد (`Converters` & `MultiBinding`)

تجنّب كتابة أصناف `IValueConverter` متكررة عندما تغطي محوّلات Avalonia المدمجة الحاجة:
- `StringConverters.IsNotNullOrEmpty` و`StringConverters.IsNullOrEmpty`
- `ObjectConverters.IsNotNull` و`ObjectConverters.IsNull`
- `BoolConverters.And` و`BoolConverters.Or` و`BoolConverters.Not`
- لكتابة محوّل مخصص خفيف في C# استخدم `FuncValueConverter<TIn, TOut>` معرّفاً كحقل `public static readonly` واستدعه عبر `{x:Static ...}` (يبقى متوافقاً مع `CompiledBindings`).
- استخدم `FallbackValue` و`TargetNullValue` على `{Binding}` لحماية الواجهة من القيم الفارغة أثناء الإقلاع أو التصفير.

---

## 4. `DataTemplates` و`IDataTemplate` و`FuncDataTemplate<T>`

خلافاً لـ WPF، يبحث Avalonia عن قوالب البيانات في خاصية `DataTemplates` على `Control` أو `Window` أو `Application` صعوداً في الشجرة المنطقية (وليس داخل `Resources`):

1. **التطابق التلقائي بالنوع في XAML:**
   ```xml
   <UserControl.DataTemplates>
     <DataTemplate DataType="models:ShapeNode">
       <TextBlock Text="{Binding Name}" />
     </DataTemplate>
   </UserControl.DataTemplates>
   ```
2. **القالب البرمجي المرن (`IDataTemplate` و`FuncDataTemplate<T>`):**
   - نفّذ `IDataTemplate` (`bool Match(object? data)` و`Control? Build(object? param)`) عند الحاجة لاختيار القالب بناءً على شروط داخل الكائن أو إعادة تدوير مكونات معقدة.
   - للحالات السريعة في C#: `new FuncDataTemplate<MyItem>((item, _) => new TextBlock { Text = item.Title })`.

---

## 5. قوائم البيانات الكبيرة و`VirtualizingStackPanel`

عند عرض مكتبة مشاريع أو طبقات أو نتائج بحث طويلة:
- يستخدم `ListBox` افتراضياً `VirtualizingStackPanel`، لكن **الافتراضية تفشل بصمت** إذا أُعطي عنصر القائمة ارتفاعاً غير محدود!
- **الشرط الإلزامي للافتراضية:**
  1. ضع `ListBox` / `ItemsControl` داخل صف `Grid` بارتفاع مقيد (`*` أو ارتفاع رقمي محدد أو `MaxHeight`) أو داخل `DockPanel` (لا تضعه أبداً داخل `StackPanel` أو `ScrollViewer` خارجي يلتف حوله).
  2. اضبط `BufferFactor` عند الحاجة لتحضير عناصر إضافية خارج نطاق العرض وتخفيف وميض التمرير السريع:
     ```xml
     <ListBox Grid.Row="1" ItemsSource="{Binding Projects}">
       <ListBox.ItemsPanel>
         <ItemsPanelTemplate>
           <VirtualizingStackPanel BufferFactor="1" />
         </ItemsPanelTemplate>
       </ListBox.ItemsPanel>
     </ListBox>
     ```
