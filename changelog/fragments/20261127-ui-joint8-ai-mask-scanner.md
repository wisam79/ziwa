---
type: feat
scope: ui
related_files:
  - src/Ziwa.Rendering/Ai/AlphaMaskCompositor.cs:260
  - src/Ziwa.App/Controls/MaskBrushCanvasControl.cs:322
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Ai.cs:97
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Scanner.cs:214
  - src/Ziwa.App/Views/AiMaskEditorView.axaml:52
  - src/Ziwa.App/Views/DocumentScannerView.axaml:75
---
صقل المفصل الثامن (محرر القناع والذكاء الاصطناعي وماسح المستندات): إضافة عملية عكس القناع (`InvertAll` مع التراجع)، وتمييز زر الفرشاة النشطة بصرياً، وزر إعادة ضبط العرض 100% واختصارات `[` / `]` و`R` في `MaskBrushCanvasControl`، وإضافة زر «كامل الصورة» لإعادة ضبط زوايا الإطار في الماسح الضوئي مع إشعارات `ShowToast` عند الاعتماد والإدراج.
