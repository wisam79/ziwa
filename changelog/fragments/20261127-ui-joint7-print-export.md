---
type: feat
scope: ui
related_files:
  - src/Ziwa.Application/Persistence/ProjectContracts.cs:81
  - src/Ziwa.App/Services/AvaloniaProjectDialogs.cs:18
  - src/Ziwa.App/ViewModels/MainWindowViewModel.PrintExport.cs:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.cs:1922
  - src/Ziwa.App/Views/MainWindow.axaml:637
---
صقل المفصل السابع (التجهيز الطباعي وحوار الطباعة والتصدير): دعم اختيار مسار التصدير عبر حوار الحفظ الرسمي (`PickSaveExportAsync`) مع الحفاظ على المسار الاحتياطي للاختبارات، وإضافة اختيار دقة التصدير (`150/300/600 DPI`) وأزرار التصدير النقطي (`PNG / JPEG`) المتعددة الصفحات مع علامات القص والنزيف، وترقية نافذة الطباعة بشريط تنبيهات `Preflight` وزر مراجعتها، وتعريب قوائم الاتجاه وملفات `CMYK ICC` واستبدال حقل عدد النسخ بـ `NumericUpDown`.
