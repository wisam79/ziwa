# مسار عمل التدقيق الأمني الدوري (`Security Audit Workflow`)

> **المرجع:** [`docs/SECURITY_AND_CLOUD.md`](../../docs/SECURITY_AND_CLOUD.md) و[`.agents/rules/platform-ai-and-security.md`](../rules/platform-ai-and-security.md).

## خطوات الفحص
1. **فحص الأسرار والحافظة:** ابحث عن أي مفتاح سري أو token ثابت في الكود، وتأكد أن كل JSON/SVG/مسار خارجي يمر بـ `SchemaValidator` و`SvgSanitizer`.
2. **فحص الحفظ الذري وGDI:** راجع مسارات الكتابة للتأكد من استخدام `AtomicFileWriter` (`Flush(true)` + استبدال ذري)، وراجع استخدام `SafeHandle` وفحص الأخطاء في طباعة GDI.
3. **فحص الخادم المحلي وAI:** تأكد من ربط `127.0.0.1` في الجسر المحلي، حدود الحمولة `413`، مهلة Modal `180s` بلا إعادة محاولة تلقائية، وإرسال `Bearer <UserJWT>` فقط.
4. **فحص Supabase RLS:** تأكد من `SET search_path` داخل `SECURITY DEFINER`، وصياغة `IS NOT TRUE`، وإغلاق صلاحيات `PUBLIC` غير المقصودة.
5. **النتيجة:** شغّل اختبارات `Ziwa.Infrastructure.Tests` المستهدفة ووثّق أي إصلاح في fragment واحد.
