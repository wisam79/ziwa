---
type: fix
scope: ui
related_files:
  - src/Ziwa.App/Views/MainWindow.axaml.cs:14
  - src/Ziwa.App/Views/Workspace/PageStripView.axaml:51
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Scanner.cs:1
---
عزل لوحة المفاتيح داخل الحوارات واستهداف الصفحة التي فُتحت منها قائمة الأوامر. يبقى محرر النص داخل اللوحة عند تصغير النافذة، ويستعيد Esc القص السابق من شريط الأدوات. عند تبديل صورة الماسح أو إلغاء الكشف لا تُستخدم مناطق أو معاينة من الصورة السابقة.
