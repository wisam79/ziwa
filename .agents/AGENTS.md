# Ziwa Studio — دستور وقواعد الوكلاء (Master Project Constitution)

> وثيقة حاكمة مقتضبة لوكلاء **Ziwa Studio**. صُممت لتمنح الوكيل أقصى وضوح وأقل ثقل ممكن، مستفيدةً من دروس **Grido Studio**. التفاصيل العملية تعيش في `5` مهارات مركّزة تحت `.agents/skills/`.

---

## 1. هوية المشروع والتقنيات
- **التطبيق:** استوديو تصميم وطباعة استوديوهات مكتبي (`Windows` أولاً، ثم `macOS` / `Linux`).
- **الإطار والواجهة:** `.NET 10 LTS` (C# 14) + `Avalonia UI 11` + `CommunityToolkit.Mvvm` + `Fluent 2 RTL` (`CompiledBindings` إجباري).
- **محرك الرسم والطباعة:** `SkiaSharp` + `HarfBuzzSharp` (`Ziwa.Rendering` — مصدر رسم واحد للشاشة `60–120fps`، والتصدير `300–600 DPI`، وVector PDF، وCMYK TIFF عبر `Magick.NET`، وWin32 GDI).
- **البيانات والأمان وAI:** `SQLite` محلي بوضع `WAL` + كتابة ذرية، ذكاء اصطناعي محلي (`OnnxRuntime` + `OpenCvSharp`) وسحابي (`Modal CPU` بمُهلة `180s` مع إلغاء صريح وتوكن المستخدم فقط)، ومصادقة/تراخيص `Supabase Auth & RLS`.

---

## 2. الثوابت الهندسية والأمنية المطلقة
1. **حظر `cd` قطعياً:** استخدم `Cwd` أو المسارات الكاملة (محمي في `.agents/hooks.json`).
2. **التبعية الطبقية الصارمة:** `App (Views/VM) → Application / Rendering → Domain` (ولا العكس). يُمنع استيراد `Avalonia.*` في `Domain` أو `Rendering` أو `Application`، ويُمنع أي I/O أو شبكة في الـ Views/ViewModels.
3. **`Preview == Export == Print`:** لا معادلتين للقص (`Crop`) أو المليمتر (`MmPerInch = 25.4`) أو الأقنعة أو الفلاتر؛ كل المسارات تستدعي `Ziwa.Domain` و`Ziwa.Rendering`.
4. **الكتابة الذرية:** أي حفظ لمشروع/إعدادات/تصدير يمر بـ `*.tmp` + `Flush(flushToDisk: true)` + `File.Move(..., overwrite: true)` مع حذف `.tmp` في `finally`.
5. **صفر أسرار في الثنائي:** كل طلب سحابي يستخدم `Authorization: Bearer <UserJWT>` فقط. حمولات JSON/الحافظة تُفحص قبل الحقن (`Finite > 0`، تعقيم مسارات وSVG).
6. **لا ازدواجية ولا كود ميت:** وحّد أي منطق متكرر في مصدر حقيقة واحد واحذف أي ملف أو دالة بلا مستوردين (`Zero Importers`). لا تُبقِ نسخاً من `clamp` أو `Abort/Cancel` أو نصوص أوامر متناثرة.
7. **اختبارات مركّزة محلياً + التحقق عبر CI:** لا تشغّل اختبارات ثقيلة شاملة بلا حاجة؛ شغّل محلياً `dotnet format --verify-no-changes`، والبناء بلا تحذيرات، والاختبارات المستهدفة `--filter`، ثم راقب `CI` بعد الدفع (`gh run list` / `gh run watch`).
8. **مسار خطي على `main`:** لا تترك فروعاً معلّقة ولا تستخدم `git commit/push --no-verify`.

---

## 3. خريطة القواعد والمهارات

### القواعد التخصصية (`.agents/rules/`)
| القاعدة | التفعيل | التركيز |
|---|---|---|
| [`.agents/rules/architecture-governance.md`](rules/architecture-governance.md) | `always_on` | الطبقات، الاستئناف، عدم الازدواجية، سلامة الحافظة والملفات |
| [`.agents/rules/avalonia-fluent-rtl.md`](rules/avalonia-fluent-rtl.md) | `glob` (`*.axaml`, Views/VMs) | `FlowDirection="RightToLeft"`، `CompiledBindings`، `Tokens.axaml`، Wait UX |
| [`.agents/rules/skia-rendering-and-print.md`](rules/skia-rendering-and-print.md) | `glob` (`Rendering`, `Printing`, `Export`) | تطابق العرض/الطباعة، `MmPerInch`، `HarfBuzz`، `Dispose()`، CMYK/PDF/GDI |
| [`.agents/rules/platform-ai-and-security.md`](rules/platform-ai-and-security.md) | `glob` (`Infrastructure`, `Platform`, `*.sql`) | الحفظ الذري، GDI، جسر الهاتف `127.0.0.1`، AI 180s، Supabase RLS |
| [`.agents/rules/quality-and-release.md`](rules/quality-and-release.md) | `glob` (`ci/scripts/*.csproj`) | `TreatWarningsAsErrors`، `Verify.Xunit`، التحقق بـ `docs-gate` والإصدارات |

### المهارات المركّزة (5 مهارات فقط في `.agents/skills/`)
- [`ziwa-architecture-map`](skills/ziwa-architecture-map/SKILL.md): خريطة `Ziwa.sln`، الإحداثيات بالمليمتر، Command/Undo، وسجل الأوامر الموحد.
- [`avalonia-fluent-ui`](skills/avalonia-fluent-ui/SKILL.md): `Tokens.axaml`، ثيم Fluent 2 RTL، `CompiledBindings`، وربط `SkiaViewportHost`.
- [`skia-print-pipeline`](skills/skia-print-pipeline/SKILL.md): `SceneRenderer`، `HarfBuzzSharp`، الأقنعة الثمانية، `CropZoom/Pan`، CMYK TIFF، PDF المتجه، وGDI.
- [`dotnet-platform-ai`](skills/dotnet-platform-ai/SKILL.md): الحفظ الذري وSQLite، `OnnxRuntime`/`OpenCvSharp`، Modal 180s، جسر الهاتف، وSupabase RLS.
- [`ziwa-qa-and-release`](skills/ziwa-qa-and-release/SKILL.md): اختبارات التطابق البصري (`Verify.Xunit`)، `Avalonia.Headless`، الحزم، وCI.

---

## 4. بروتوكول التوثيق المختصر (`Fragment-First`)
1. **عند بدء أي جلسة:** اقرأ «مؤشر الاستئناف الحي» في [`docs/DEVELOPMENT_PLAN.md`](../docs/DEVELOPMENT_PLAN.md) والتزم بالمرحلة النشطة؛ لا تقفز لميزات لاحقة أو مؤجلة دون إذن المالك.
2. **قبل الالتزام:** لأي تعديل كود، أنشئ **fragment واحداً** في `changelog/fragments/YYYYMMDD-<scope>-<slug>.md` وفق [`changelog/README.md`](../changelog/README.md) (لا تعدّل `CHANGELOG.md` يدوياً)، ثم مرّر `node scripts/docs-gate.mjs --strict-refs` + `dotnet format Ziwa.sln --verify-no-changes`.
3. **بعد الدفع (بإذن المالك):** راقب `gh run list --limit 3` و`gh run watch` حتى نجاح `100%`.
