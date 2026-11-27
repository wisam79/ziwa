---
type: feat
scope: infra
related_files:
  - src/Ziwa.Domain/Platform/AppUpdateAndLogModels.cs:1
  - src/Ziwa.Infrastructure/Diagnostics/SerilogAppLogger.cs:1
  - src/Ziwa.Infrastructure/Updates/SecureAppUpdaterService.cs:1
  - src/Ziwa.Platform.Windows/SingleInstance/WindowsSingleInstanceCoordinator.cs:1
  - src/Ziwa.Platform.Windows/Shell/WindowsFileAssociationService.cs:1
  - src/Ziwa.App/Views/Workspace/WindowTitleBarView.axaml:1
  - src/Ziwa.App/Views/AppUpdateView.axaml:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:45
---
تنفيذ P5-03: شريط العنوان المدمج (`ExtendClientArea`)، حارس المثيل الواحد واقتران `.ziwa`، تصفية إفلات الصور بالتوقيع السحري، سجلات `Serilog` الدوّارة المنقّاة وتصدير أرشيف الدعم، والمحدّث الآمن بتحقق `HTTPS + SHA-256 Fail-Closed`.
