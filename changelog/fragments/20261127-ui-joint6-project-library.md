---
type: feat
scope: ui
related_files:
  - src/Ziwa.Application/Persistence/ProjectContracts.cs:44
  - src/Ziwa.Infrastructure/Persistence/SqliteProjectRepo.cs:62
  - src/Ziwa.Application/Persistence/ProjectWorkspace.cs:168
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Projects.cs:29
  - src/Ziwa.App/Views/ProjectLibraryView.axaml:28
  - src/Ziwa.App/Views/MainWindow.axaml:1672
---
صقل المفصل السادس (مكتبة المشاريع والبدء السريع): إضافة قسم البدء السريع بالقوالب داخل نافذة مكتبة المشاريع، وشريط البحث والتصفية الفورية، وتنسيق تواريخ الحفظ والحالة الفارغة وفتح المشروع بالنقر المزدوج، ودعم إزالة مرجع المشروع من فهرس المكتبة دون حذف الملف (`RemoveAsync`)، وإتاحة إعادة تسمية عنوان المشروع من المفتش مع التراجع الكامل.
