# خدمات TopLevel وإطار النافذة والألواح العائمة والوصولية في Avalonia 11.3

> مرجع تفصيلي لخدمات المنصة، تخصيص شريط العنوان، مقارنة الألواح المنبثقة، ومعايير الوصولية في `Ziwa.App`.

---

## 1. خدمات `TopLevel`: الملفات والحافظة والإشعارات

`TopLevel` هي الجذر المرئي للنافذة (`Window` يرث منها). احصل عليها من أي عنصر داخل الشجرة عبر `TopLevel.GetTopLevel(this)`.

### أ. منتقي الملفات (`IStorageProvider`) — النمط المعتمد في [`AvaloniaProjectDialogs.cs`](../../../../src/Ziwa.App/Services/AvaloniaProjectDialogs.cs)
```csharp
if (Owner?.StorageProvider is not { CanOpen: true } provider)
    return null;

IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
{
    Title = "إدراج صورة",
    AllowMultiple = false,
    FileTypeFilter = [ImageType],
});
ct.ThrowIfCancellationRequested();
return files.FirstOrDefault()?.TryGetLocalPath();
```
- تحقّق دائماً من `CanOpen` و`CanSave` قبل استدعاء المنتقي (في بيئة الاختبار `Headless` أو البيئات المقيدة قد تكون `false`).
- في الحفظ (`SaveFilePickerAsync`)، اضبط `SuggestedFileName`, `DefaultExtension`, `FileTypeChoices`, و`ShowOverwritePrompt = true`.
- لا تسند أي عمل مع `IStorageProvider` داخل ViewModel مباشرة؛ اعزله خلف واجهة `IProjectDialogs` في `Ziwa.Application`.

### ب. الحافظة (`IClipboard`)
- احصل عليها عبر `topLevel?.Clipboard` واستعمل `await clipboard.SetTextAsync(...)` و`await clipboard.GetTextAsync()`.
- في `Ziwa`: يُفحص أي نص أو JSON قادم من الحافظة عبر `SchemaValidator` والتحقق من القيم المنتهية الموجبة قبل إدراجه في المستند.

### ج. الإشعارات (`WindowNotificationManager`)
- مثالية لرسائل التأكيد والتنبيه السريعة (`4s`) دون مقاطعة تدفق العمل بحوار حاجز.

---

## 2. إطار النافذة المخصص (`ExtendClientAreaToDecorationsHint`)

تعتمد [`MainWindow.axaml`](../../../../src/Ziwa.App/Views/MainWindow.axaml#L16-L18) إزالة شريط النظام واستبداله بشريط أوامر مخصص (`WindowTitleBarView`):
```xml
ExtendClientAreaToDecorationsHint="True"
ExtendClientAreaChromeHints="NoChrome"
ExtendClientAreaTitleBarHeightHint="32"
```

### التفاصيل الحرجة عند تطبيق الكروم المخصص
1. **تعويض هوامش التكبير على ويندوز (`OffScreenMargin`):** عند تكبير نافذة بلا حدود، يمدّد ويندوز حدود النافذة خارج الشاشة بضع بكسلات؛ لذا يلزم في [`MainWindow.axaml.cs`](../../../../src/Ziwa.App/Views/MainWindow.axaml.cs#L93-L100):
   ```csharp
   protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   {
       base.OnPropertyChanged(change);
       if (change.Property == WindowStateProperty)
       {
           Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default;
       }
   }
   ```
2. **سحب النافذة وتبديل التكبير:** مرّر `BeginMoveDrag(e)` في شريط العنوان مع استثناء الأزرار التفاعلية، واجعل زر الإغلاق والتكبير والتصغير واضحة في الطرف الصحيح لاتجاه `RightToLeft`.

---

## 3. اختيار الحاوية العائمة المناسبة (`Dialogs`, `Flyout`, `Popup`, `AdornerLayer`)

1. **`StudioDialogShell` ([`StudioControlThemes.axaml`](../../../../src/Ziwa.App/Themes/StudioControlThemes.axaml)):**
   - الخيار الأول لجميع شاشات الاستوديو الداخلية (الطباعة والتصدير، استوديو الملصقات، الماسح الضوئي، الحساب).
   - يمنح تعتيم موحد (`PART_Scrim`)، رأساً وذيلاً ثابتين، وتمرير جسم متوافق مع `RTL`.
2. **`Window.ShowDialog<TResult>(owner)`:**
   - يُستخدم للحوارات النظامية الضرورية قبل إغلاق التطبيق (`ConfirmUnsavedChangesAsync`).
   - سجّل `CancellationToken` لإغلاقه بأمان عبر `Dispatcher.UIThread.Post(() => dialog.Close(...))`.
3. **`Flyout` و`MenuFlyout`:**
   - اربطه عبر `Button.Flyout` أو `FlyoutBase.AttachedFlyout` لقوائم «المزيد» وخيارات القوالب؛ يغلق تلقائياً بالنقر خارجه (`Light-dismiss`).
4. **`ContextMenu`:**
   - إذا أُرفق بعنصر داخل جزيرة `FlowDirection="LeftToRight"` (مثل [`SkiaViewportHost.cs`](../../../../src/Ziwa.App/Controls/SkiaViewportHost.cs))، اضبط `<ContextMenu FlowDirection="RightToLeft">` صراحةً.
5. **`Popup` و`AdornerLayer`:**
   - `Popup` للعرض العائم منخفض المستوى؛ و`AdornerLayer` لطبقة إرشاد أو تركيز فوق العناصر دون المساس بترتيب التخطيط.

---

## 4. الوصولية والأتمتة (`Accessibility & Automation`)

يدعم Avalonia 11 شجرة الوصولية عبر منصات النظام (`UI Automation` على Windows، و`NSAccessibility` على macOS، و`AT-SPI` على Linux):
- **`AutomationProperties.Name`:** إلزامي بالعربية لكل زر أيقوني، شريط تمرير، وحقل إدخال (`StudioNumberField` يمرّر `AccessibleName`).
- **`AutomationProperties.HelpText` و`LabeledBy`:** لربط وصف إضافي أو ربط حقل بعنوانه البصري دون تكرار النص.
- **`PathIcon` داخل الأزرار:** اضبط `IsHitTestVisible="False"` على الأيقونة حتى لا يعترض مؤشر القارئ أو اختبار النقر منتصف الزر.
- **مؤشر التركيز `:focus-visible`:** يظهر تلقائياً عند التنقل بـ `Tab` دون تشويه النقر بالماوس (معرّف في `WorkspaceStyles.axaml`).
- **`AutomationPeer` مخصص:** عند بناء عنصر تحكم تفاعلي فريد يحتاج تعريف دور وحالة مخصصة لقارئ الشاشة، تجاوز `OnCreateAutomationPeer()` في الصنف.
