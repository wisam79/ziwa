---
type: feat
scope: cloud
related_files:
  - src/Ziwa.Domain/Auth/AuthAndLicensingModels.cs:1
  - src/Ziwa.Application/Abstractions/ISupabaseAuthService.cs:1
  - src/Ziwa.Application/Auth/AccountSessionCoordinator.cs:1
  - src/Ziwa.Infrastructure/Auth/SupabaseAuthOptions.cs:1
  - src/Ziwa.Infrastructure/Auth/AtomicLocalAuthSessionStore.cs:1
  - src/Ziwa.Infrastructure/Auth/LoopbackOAuthReceiver.cs:1
  - src/Ziwa.Infrastructure/Auth/SupabaseAuthClient.cs:1
  - src/Ziwa.App/ViewModels/MainWindowViewModel.Account.cs:1
  - src/Ziwa.App/Views/AccountLicenseView.axaml:1
  - docs/DEVELOPMENT_PLAN.md:1
  - docs/SECURITY_AND_CLOUD.md:1
---
تنفيذ `P5-01`: بناء عميل المصادقة والتراخيص (`SupabaseAuthClient`) ونافذة الحساب (`AccountLicenseView`) المتوافقة مع مشروع Supabase القائم في Grido (التسجيل والدخول بالبريد، التحقق وإعادة الإرسال بـ `OTP`، استعادة كلمة المرور، دخول `Google OAuth` عبر مستمع استرجاع محلي عشوائي المنفذ `LoopbackOAuthReceiver` بتحقق `Origin` و`state`، وتفعيل مفاتيح التراخيص `activate_license`).
تطبيق عقد الجلسة الواحدة النشطة (`Last-Wins` عبر `claim_session`/`check_session`)، وحماية تدوير `refresh_token` عبر قفل تسلسلي وتبنّي الحالة الأحدث (`SEC-08`)، ومنع إحياء الجلسة بعد تسجيل الخروج (`_transitionGeneration`)، والتخزين المحلي المشفّر `AES-GCM` الموقَّع بـ `HMAC-SHA256` مع مزامنة الحصة اليومية عبر `AccountSessionCoordinator` دون تسجيل أي توكن.
