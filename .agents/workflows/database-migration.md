# مسار عمل ترحيلات قواعد البيانات (`Database Migration Workflow`)

> **المرجع:** [`.agents/skills/dotnet-platform-ai/SKILL.md`](../skills/dotnet-platform-ai/SKILL.md) و[`docs/SECURITY_AND_CLOUD.md`](../../docs/SECURITY_AND_CLOUD.md).

## ينطبق على SQLite المحلي وSupabase Postgres
1. **المعاملة والتوافق الرجعي:** كل ترحيل رقمي متسلسل يقبل الاسترداد أو الترقية النظيفة لبيانات المشاريع القديمة دون كسر `VERSION=1`.
2. **ضوابط Postgres / Supabase:**
   - تفعيل `ALTER TABLE ... ENABLE ROW LEVEL SECURITY;` مع كل جدول جديد.
   - استخدام `IS NOT TRUE` في استثناءات الحظر، وتثبيت `SET search_path = public, pg_temp;` لأي دالة `SECURITY DEFINER`.
   - فهرسة مفاتيح الـ RLS (`user_id`, `session_id`) لتفادي بطء الفحص المتكرر.
3. **التحقق:** اختبر الترحيل محلياً على نسخة قاعدة بيانات نموذجية وشغّل اختبارات `Persistence` المستهدفة ثم `docs-gate`.
