---
type: feat
scope: rendering
related_files:
  - Directory.Build.props:1
  - Directory.Packages.props:1
  - Ziwa.sln:1
  - src/Ziwa.Domain/Math/DomainMath.cs:1
  - src/Ziwa.Domain/Validation/SchemaValidator.cs:1
  - src/Ziwa.Rendering/SceneRenderer.cs:1
  - src/Ziwa.Rendering/Typography/HarfBuzzArabicShaper.cs:1
  - src/Ziwa.Rendering/Export/PngJpegExporter.cs:1
  - src/Ziwa.Infrastructure/IO/AtomicFileWriter.cs:1
  - src/Ziwa.App/Themes/Tokens.axaml:1
  - src/Ziwa.App/Controls/SkiaViewportHost.cs:1
  - docs/DEVELOPMENT_PLAN.md:1
---
إنجاز المرحلة 0 بالكامل (`P0-01` .. `P0-05`): تأسيس الحل `Ziwa.sln` والمشاريع الـ 11 مع عزل الطبقات، نواة `Ziwa.Domain` بالمليمتر، محرك `Ziwa.Rendering` مع التشكيل العربي `HarfBuzz` وكاش المعاينة وتصدير `300/600 DPI` بحقن `pHYs`/`JFIF`، وواجهة `Avalonia Fluent 2 RTL` مع مضيف `SkiaViewportHost` واختبارات شاملة عبر المشاريع الخمسة.
