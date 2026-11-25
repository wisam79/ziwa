# مسار عمل الشريحة العمودية والمراحل (`Vertical Slice Workflow`)

> **المرجع الوحيد للخارطة:** [`docs/DEVELOPMENT_PLAN.md`](../../docs/DEVELOPMENT_PLAN.md) وقواعد الحوكمة في [`.agents/rules/architecture-governance.md`](../rules/architecture-governance.md).

## متى يُستخدم؟
عند بدء أي مهمة من [`docs/DEVELOPMENT_PLAN.md`](../../docs/DEVELOPMENT_PLAN.md) لتسليم شريحة عاملة من البداية للنهاية دون توسيع غير مطلوب.

## الخطوات
1. **قراءة مؤشر الاستئناف الحي:** حدّد المهمة النشطة ومعيار قبولها في `docs/DEVELOPMENT_PLAN.md`.
2. **التصميم المحدود بالطبقات:** حدّد ما يقع في `Domain` ثم `Rendering` ثم `Application` ثم `Infrastructure/App` قبل كتابة الكود؛ تحقّق من عدم وجود مثيل له لإعادة استخدامه بدل التكرار.
3. **التنفيذ والاختبار المستهدف:**
   - نفّذ الكود مع احترام `TreatWarningsAsErrors` و`CompiledBindings`.
   - شغّل الاختبار المستهدف (`dotnet test --filter ...`) + `dotnet format --verify-no-changes`.
4. **مزامنة التوثيق:** أنشئ `fragment` واحداً في `changelog/fragments/`، حدّث الحالة و«مؤشر الاستئناف الحي» في `docs/DEVELOPMENT_PLAN.md` بالأدلة الفعلية فقط، ومرّر `node scripts/docs-gate.mjs --strict-refs`.
5. **الالتزام والدفع (بعد إذن المالك):** التزم بـ Conventional Commits ثم راقب `gh run list` و`gh run watch` حتى اكتمال `CI`.
