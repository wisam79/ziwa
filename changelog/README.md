# Changelog — معمارية Fragments في Ziwa Studio

> لمنع تضخم سجل واحد (مثل مشكلة `CHANGELOG` الضخم تاريخياً)، تعتمد **Ziwa** من يومها الأول نمط **`Fragment-First`**:
> كل جلسة تطوير تنشئ ملف fragment صغيراً في `changelog/fragments/`، ويُجمع السجل عند الإصدار فقط.

---

## القواعد المختصرة
1. **لا تكتب سجل الجلسة في `CHANGELOG.md` يدوياً** — أضف ملفاً واحداً لكل جلسة بصيغة:
   `changelog/fragments/YYYYMMDD-<scope>-<slug>.md`
2. كل ملف fragment يحتوي على سكيمة YAML واضحة في الأعلى مع مسارات حقيقية قابلة للتحقق بواسطة `scripts/docs-gate.mjs`:

```yaml
---
type: feat         # feat | fix | perf | refactor | docs | security | removed
scope: rendering   # domain | rendering | ui | print | ai | auth | infra | docs
related_files:
  - docs/DEVELOPMENT_PLAN.md:1
---
ملخص واضح بالعربي لما تغيّر ولماذا في سطر إلى ثلاثة أسطر.
```

3. تفحص بوابة `node scripts/docs-gate.mjs --strict-refs` وجود جميع الملفات المذكورة في `related_files` وصحة `type`.
