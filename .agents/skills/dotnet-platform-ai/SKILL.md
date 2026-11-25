---
name: dotnet-platform-ai
description: الحفظ الذري وSQLite، التكامل مع Windows GDI، جسر الهاتف عبر Kestrel، الذكاء الاصطناعي المحلي/السحابي، وأمان Supabase RLS
---

# خدمات المنصة والأمان والذكاء الاصطناعي (`Ziwa.Infrastructure` & `Ziwa.Platform.Windows`)

المرجع الكامل: [`docs/SECURITY_AND_CLOUD.md`](../../../docs/SECURITY_AND_CLOUD.md).

---

## 1. الحفظ الذري وSQLite والمشاريع
- **`AtomicFileWriter`:** لا تكتب ملفاً مهماً مباشرة؛ اكتب إلى ملف مؤقت في نفس المجلد، نفّذ `await stream.FlushAsync(ct);` متبوعاً بتفريغ نظام الملفات `stream.Flush(flushToDisk: true);` ثم `File.Move(tmp, target, overwrite: true)`؛ احذف `.tmp` في `finally`.
- **SQLite:** فعّل `JournalMode = WAL` و`ForeignKeys = True`. استخدم أوامر معلمية (`Parameters.AddWithValue`) فقط ولا تبنِ SQL بدمج النصوص أبداً.
- **فحص المدخلات:** كل `ProjectDto` أو حزمة لصق في الحافظة تُمرر عبر `SchemaValidator` لفحص أن جميع الأبعاد موجبة ومنتهية، وأن `Scale`/`Opacity`/`Zoom` ضمن الحدود المعقولة.

---

## 2. طباعة Win32 GDI المباشرة وسحب الملفات
- استخدم `SafeHandle` لكل مقبض GDI (`HDC`, `HGLOBAL` للـ `DEVMODE`, `HANDLE` للطابعة) لتحرير الموارد حتمياً حتى مع الاستثناءات.
- حوّل المقاسات المليمترية إلى نقاط الطابعة بناءً على `GetDeviceCaps(LOGPIXELSX/Y)` و`PHYSICALOFFSETX/Y` مع فحص دقيق لعدم تخطي منطقة الطباعة.
- عند السحب من Explorer (`DragDrop`): صَفِّ الملفات غير الصورية عبر التوقيع السحري قبل تحميلها للمحرر.

---

## 3. جسر الهاتف المحلي والخادم الآمن
- استمع على `http://127.0.0.1:<port>` عند التشغيل المحلي.
- تطلّب `X-Bridge-Token` أو بارامتر جلسة مؤقتاً عشوائياً لكل اقتران.
- استخدم `[RequestSizeLimit(...)]` أو قراءة محدودة السقف تُعيد `413` عند تجاوز الحد.
- افحص التوقيع السحري للصورة، وإذا عُرضت عبر خادم محلي أرفق `Content-Security-Policy: default-src 'none'; sandbox;` و`X-Content-Type-Options: nosniff`.

---

## 4. مسار AI (`OnnxRuntime` + `OpenCvSharp` + `Modal CPU`)
- **محلياً:** عزل الخلفية بـ `OnnxRuntime` وكشف/استعدال المستندات بـ `OpenCvSharp` يدار في `Task.Run` مع تمرير `CancellationToken` وفحص `ct.ThrowIfCancellationRequested()`.
- **سحابياً (`Modal`):**
  - مهلة `HttpClient` الصريحة **`180` ثانية** لكل طلب مع `CancellationToken` مربوط بزر الإلغاء، **ولا تستخدم Polly Retry التلقائي** حتى لا تُستنزف حصة المستخدم عند انقطاع أو تأخر السحابة.
  - أرفق `Authorization: Bearer <UserJWT>` فقط — صفر مفاتيح سرية مدمجة في الثنائي.
  - لا تسجل التوكن أو بيانات الصورة الخام في ملفات السجل (`Serilog` / `ILogger` يسجل `byteLength` وحالة الاستجابة فقط).

---

## 5. Supabase Auth & RLS
- كل دالة Postgres مرفوعة الصلاحيات (`SECURITY DEFINER`) تفرض `SET search_path = public, pg_temp;`.
- في شروط المنع (`banned`/`suspended`): استخدم `is_suspended IS NOT TRUE` لا `is_suspended = false` لتجنب تسرب قيم `NULL`.
- تحقق من أخطاء الشبكة عند `RefreshSession` ولا تسجل خروجاً تلقائياً إلا عند رفض مصادق عليه من الخادم.
