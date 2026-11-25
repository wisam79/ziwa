---
trigger: glob
glob: '{**/Ziwa.Infrastructure/**/*.cs,**/Ziwa.Platform.*/**/*.cs,**/Ai*/**/*.cs,**/*.sql}'
description: 'الأمان المحلي والشبكي، الحفظ الذري، GDI، جسر الهاتف، AI، وSupabase RLS'
---

# قواعد المنصة والأمان والذكاء الاصطناعي والسحابة

المصدر التفصيلي: `.agents/skills/dotnet-platform-ai/SKILL.md` و`docs/SECURITY_AND_CLOUD.md`.

- **الحفظ الذري والـ CrashGuard:** لا تستخدم `File.WriteAllText/Bytes` للبيانات الدائمة؛ استعمل `AtomicFileWriter` (`*.tmp` + `Flush(true)` + `File.Move(..., overwrite: true)`). فعّل SQLite WAL والترحيلات المرقّمة (`SchemaMigrations`).
- **أمان Win32 GDI وP/Invoke:** غلّف كل مقبض GDI/HDC/DEVMODE داخل `SafeHandle`؛ تحقق من قيم الإرجاع وأطلق خطأً صريحاً عند فشل Spooler بدلاً من الفشل الصامت. عند سحب الملفات الأصلي من Explorer تحقق من التوقيع السحري قبل فتح الملف.
- **جسر الهاتف والخادم المحلي:** استمع حصراً على `127.0.0.1` في الوضع المحلي، اطلب رمز اقتران جلسة عشوائي، حدّد حجم الحمولة قبل القراءة (`413 PayloadTooLarge`)، افحص التوقيع السحري للصور، واحظر Path Traversal وSVG الضار (`SvgSanitizer`).
- **مسار AI المحلي والسحابي:**
  - `OnnxRuntime` و`OpenCvSharp` يعملان في خيط خلفي مع فحص `CancellationToken`.
  - عميل `Modal CPU` يرسل `Authorization: Bearer <UserJWT>` فقط (صفر أسرار ثابتة)، بمهلة `180s` بدون إعادة محاولة تلقائية، ويمرر معرّف العملية/الهدف لمنع السباقات بعد الإلغاء أو التبديل.
- **Supabase & RLS:** دوال `SECURITY DEFINER` تثبت `SET search_path = public, pg_temp;` وتُقيّد `GRANT`. سياسات الفحص تستخدم `IS NOT TRUE` في استثناءات الحظر/التعليق، وحالة الجلسة الواحدة تحكمها قاعدة البيانات، ولا تُمسح الجلسة المحلية بسبب خطأ شبكة عابر.
