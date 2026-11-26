---
type: refactor
scope: ui
related_files:
  - src/Ziwa.App/Views/MainWindow.axaml:23
  - src/Ziwa.App/Views/Workspace/CommandBarView.axaml:1
  - src/Ziwa.App/Views/Workspace/WorkspaceRailView.axaml:1
  - src/Ziwa.App/Views/Workspace/PageStripView.axaml:1
  - src/Ziwa.App/Views/Workspace/WorkspaceStatusBarView.axaml:1
  - src/Ziwa.App/ViewModels/PageTabItemViewModel.cs:1
  - src/Ziwa.App/Themes/Icons.axaml:1
  - src/Ziwa.App/Themes/WorkspaceStyles.axaml:1
  - src/Ziwa.App/Themes/Tokens.axaml:1
  - tests/Ziwa.App.Tests/WorkspaceChromeHeadlessTests.cs:1
  - docs/DESIGN_SYSTEM.md:27
  - docs/DEVELOPMENT_PLAN.md:13
---
تجديد مساحة العمل RTL بأيقونات متجهة موحدة وأربعة بلوكات مستقلة للأوامر والأدوات والصفحات والحالة. تُختصر العناوين الطويلة دون إزاحة الأزرار، وتُمرّر ألسنة الصفحات دون إخفاء إجراءاتها، مع الحفاظ على أوامر المحرّر الحالية.
فصل إحداثيات الورقة والمساطر ومعاينات الصور عن انعكاس الواجهة، وضبط تدرّج أحجام الأيقونات والخطوط وكثافة الأشرطة.
