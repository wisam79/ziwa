---
type: fix
scope: ui
related_files:
  - src/Ziwa.App/Themes/Tokens.axaml:9
  - src/Ziwa.App/Themes/StudioControlThemes.axaml:1
  - src/Ziwa.App/Themes/AppleHigStyles.axaml:25
  - src/Ziwa.App/Controls/StudioDialogShell.cs:1
  - src/Ziwa.App/Controls/StudioCard.cs:1
  - src/Ziwa.App/Controls/StudioNumberField.cs:1
  - src/Ziwa.App/Controls/StudioSliderRow.cs:1
  - src/Ziwa.App/Views/MainWindow.axaml:1
  - src/Ziwa.App/Views/Inspector/InspectorPanelView.axaml:1
  - src/Ziwa.App/Views/Dialogs/PrintExportDialogView.axaml:1
  - src/Ziwa.App/Views/Dialogs/StickerStudioView.axaml:1
  - src/Ziwa.App/Views/Dialogs/CommandPaletteView.axaml:1
  - src/Ziwa.Rendering/Svg/SvgArabicTextOutliner.cs:1
  - src/Ziwa.Rendering/Assets/AssetStore.cs:109
  - docs/DESIGN_SYSTEM.md:43
---
إعادة بناء معمارية واجهة الاستوديو بمكونات قياسية دائمة وتشكيل النصوص العربية في SVG: إنشاء مكونات `StudioDialogShell` و`StudioCard` و`StudioNumberField` و`StudioSliderRow` مع `ControlTheme` في `StudioControlThemes.axaml`، تفكيك `MainWindow.axaml` إلى `InspectorPanelView` و`PrintExportDialogView` و`StickerStudioView` و`CommandPaletteView` وتوحيد النوافذ الست فوقها، توحيد التعتيم وتباين الثيم الداكن، وتشكيل نصوص SVG العربية بـ `HarfBuzz` قبل تمريرها إلى `SKSvg`.
