---
type: feat
scope: ui
related_files:
  - src/Ziwa.Domain/Stickers/ParametricStickerCatalog.cs:78
  - src/Ziwa.Domain/Stickers/OklchPaletteGenerator.cs:62
  - src/Ziwa.Application/Interaction/FreeformSceneOperations.cs:483
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Inspector.cs:90
  - src/Ziwa.App/ViewModels/MainWindowViewModel.cs:1178
  - src/Ziwa.App/Views/MainWindow.axaml:523
  - src/Ziwa.App/Views/Workspace/WorkspaceRailView.axaml:23
---
صقل المفصل الخامس (استوديو الملصقات البارامتري والنص المنحني): إضافة قالب الختم الرسمي البيضاوي `oval-official-stamp` واشتقاق باقة ألوان OKLCH من اللون الأساسي المختار، وإضافة معاينة Skia حية وشرائح ألوان داخل استوديو الملصقات وبطاقة المفتش مع إصلاح تبديل القالب أثناء تعديل ملصق قائم، وتزويد محرر النصوص بضوابط نوع وشدة الانحناء (`ArcUp/ArcDown/FullCircle`) والسُمك وتباعد الأحرف والحدود والكبسولة والظل وقائمة قوالب النصوص الكاملة في شريط الأدوات.
