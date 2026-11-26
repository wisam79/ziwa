---
type: feat
scope: infra
related_files:
  - src/Ziwa.Application/Persistence/ProjectContracts.cs:1
  - src/Ziwa.Application/Persistence/ProjectWorkspace.cs:1
  - src/Ziwa.Application/Persistence/ProjectDocumentSerializer.cs:1
  - src/Ziwa.Infrastructure/Persistence/ZiwaProjectStore.cs:1
  - src/Ziwa.Infrastructure/Persistence/SqliteProjectRepo.cs:1
  - src/Ziwa.Infrastructure/Persistence/CrashGuard.cs:1
  - src/Ziwa.Infrastructure/IO/AtomicFileWriter.cs:1
  - src/Ziwa.App/Views/ProjectLibraryView.axaml:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Projects.cs:1
  - src/Ziwa.Rendering/Svg/SvgSanitizer.cs:1
  - tests/Ziwa.Infrastructure.Tests/Phase4PersistenceTests.cs:1
  - tests/Ziwa.Application.Tests/Phase4ProjectWorkspaceTests.cs:1
  - tests/Ziwa.App.Tests/Phase4ProjectHeadlessTests.cs:1
  - tests/Ziwa.Rendering.Tests/Phase4SvgSecurityTests.cs:1
  - docs/SECURITY_AND_CLOUD.md:1
  - docs/DEVELOPMENT_PLAN.md:1
---
تنفيذ `P4-01`: حزم `.ziwa` إصدارية بالأصول الأصلية وبصمات SHA-256، كتابة ذرية متدفقة وفحص آمن قبل الفتح، ومكتبة SQLite WAL مع نسخ `.ziwabackup` لا تستبدل المشاريع الموجودة.
إضافة دورة حياة الحفظ/الفتح وحماية التعديلات غير المحفوظة، وCrashGuard بحفظ تلقائي مستقل وقفل جلسة وعرض الاستعادة، وربط الأوامر والحوارات والمكتبة بالواجهة العربية مع توكنز Apple HIG.
تغطية دورات الحفظ والاستعادة والفشل والإلغاء والسباقات والنسخ الاحتياطية والوصولية باختبارات مستهدفة، وتشديد تعقيم SVG لمنع مراجع CSS الخارجية.
