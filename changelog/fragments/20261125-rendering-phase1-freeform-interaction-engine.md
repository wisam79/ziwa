---
type: feat
scope: rendering
related_files:
  - src/Ziwa.Domain/Models/SceneNodes.cs:1
  - src/Ziwa.Domain/Validation/SchemaValidator.cs:1
  - src/Ziwa.Rendering/Geometry/MaskPathBuilder.cs:1
  - src/Ziwa.Rendering/Svg/SvgSanitizer.cs:1
  - src/Ziwa.Rendering/Typography/TextOnCurveRenderer.cs:1
  - src/Ziwa.Rendering/SceneRenderer.cs:1
  - src/Ziwa.Application/Session/DocumentSession.cs:1
  - src/Ziwa.Application/Commands/CommandRegistry.cs:1
  - src/Ziwa.Application/Interaction/HitTestEngine.cs:1
  - src/Ziwa.Application/Interaction/SnapEngine.cs:1
  - src/Ziwa.Application/Interaction/FreeformSceneOperations.cs:1
  - src/Ziwa.App/Controls/CanvasRulerControl.cs:1
  - src/Ziwa.App/Controls/SkiaViewportHost.cs:1
  - docs/DEVELOPMENT_PLAN.md:1
---
إنجاز المرحلة 1 بالكامل (`P1-01` .. `P1-06`): جلسة المستند ثنائية الطور وسجل `UndoRedoStack` بدمج الحركات (`Nudge Coalescing`) و`CommandRegistry` الموحد، محرك `HitTestEngine` ومقابض التحويل والتجميع واللصق المحمي، المساطر المثبتة (`20px`) والمحاذاة المغناطيسية (`SnapEngine`)، الأقنعة الثمانية والقص الموضعي غير الهدّام، الأشكال العشرة وتعقيم وتلوين `SvgNode` عبر `Svg.Skia`، والنص العربي المنحني عبر `HarfBuzz` و`SKPathMeasure`.
