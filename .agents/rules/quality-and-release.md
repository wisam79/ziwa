---
trigger: glob
glob: '{**/*.sln,**/*.csproj,**/.github/**,scripts/**,tests/**}'
description: 'قواعد الجودة، الاختبارات المستهدفة، البناء بلا تحذيرات، وبوابات الإصدار'
---

# قواعد الجودة والاختبارات والإصدار (Quality & Release)

المصدر التفصيلي: `.agents/skills/ziwa-qa-and-release/SKILL.md`.

- **صفر تحذيرات وأسلوب موحّد:** يُفعل `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` و`<Nullable>enable</Nullable>` عبر `Directory.Build.props`. تحقق التنسيق يتم بـ `dotnet format Ziwa.sln --verify-no-changes`.
- **حماية الجهاز المحلي أثناء العمل:** نفّذ اختبارات مستهدفة سريعة بالمرشح (`dotnet test --filter "FullyQualifiedName~<Target>"`) بدلاً من تشغيل حزم الاختبارات الثقيلة أو فحص الذاكرة الكامل في كل تعديل صغير، ودع التغطية الكاملة والحزم الإجمالية لمسار `CI`.
- **اختبارات التطابق البصري للرسم:** أي تعديل في `Ziwa.Rendering` أو `MaskPathBuilder` أو `CropViewportMath` يتطلب اختبار `Verify.Xunit` يُقارن لقطة العرض مع ناتج التصدير `300 DPI` أو يثبت تطابق البكسلات والإحداثيات.
- **اختبارات الواجهة بلا متصفح (`Avalonia.Headless.XUnit`):** اختبار لوحات المفتش، اختصارات الأوامر، التدفق العربي `RTL`، وسباقات العمليات دون الحاجة لنافذة نظام ثقيلة.
- **الإصدار والتحقق الأمني:** حزم التثبيت والتحقق ترفق ملف `checksums` + تحقق SHA-256 صارم (`fail-closed`) قبل تطبيق أي تحديث، وكل كود مدفوع يمر عبر `docs-gate` وبوابة CI الخضراء 100%.
