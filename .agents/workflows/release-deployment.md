# مسار عمل الإصدار والترفيع (`Release Deployment Workflow`)

> **المرجع:** [`.agents/skills/ziwa-qa-and-release/SKILL.md`](../skills/ziwa-qa-and-release/SKILL.md) و[`changelog/README.md`](../../changelog/README.md).

## الخطوات
1. **نظافة الشجرة وتجميع التغييرات:** تحقق من كل `changelog/fragments/*.md` المفتوحة وعدم وجود تعديلات غير مفرّغة.
2. **توحيد رقم الإصدار:** طابق `<Version>` في `Directory.Build.props` وبيان المثبّت (`Velopack`/`InnoSetup`) وإصدار ملاحظات الإصدار.
3. **الفحص النهائي المحلي + السحابي:**
   - `dotnet format Ziwa.sln --verify-no-changes`
   - `node scripts/docs-gate.mjs --strict-refs`
   - بناء الـ Release عبر GitHub Actions وإصدار `checksums.txt` (SHA-256 fail-closed للمحدّث التلقائي).
4. **المراقبة بعد الدفع:** راقب `gh run list` و`gh run watch` وتأكد من خلو خط أنابيب الإصدار من أي فشل أو تخطٍ لبوابة مطلوبة.
