---
trigger: glob
glob: '{**/*.axaml,**/ViewModels/**/*.cs,**/Views/**/*.cs,**/Controls/**/*.cs}'
description: 'قواعد واجهة Avalonia UI 11 ومعايير Fluent 2 ودعم RTL والربط المصرّف'
---

# قواعد واجهة Avalonia وFluent 2 RTL

المصدر التفصيلي: `.agents/skills/avalonia-fluent-ui/SKILL.md` و`docs/DESIGN_SYSTEM.md`.

- **RTL في الجذر (`FlowDirection`):** تحدد النوافذ الجذرية `FlowDirection="RightToLeft"`. لا تضع `FlowDirection="LeftToRight"` إلا حيث يلزم تقنياً (أكواد Hex، أبعاد W×H، أرقام حسابية). استعمل `DockPanel`/هوامش متناظرة وأيقونات تعكس الاتجاه أو تلتزم باتجاه القراءة العربي الصحيح.
- **الربط المصرّف (`CompiledBindings`) إلزامي:** كل ملف `.axaml` يصرّح بـ `x:DataType="..."`؛ يُمنع `ReflectionBinding` إلا عند الضرورة القصوى المبررة بتعليق.
- **توكنز `Tokens.axaml` فقط (Apple HIG):** يُمنع كتابة ألوان Hex خام أو `Brush` مباشر أو مقاس استدارة شاذ داخل `.axaml`؛ استعمل `DynamicResource` و`StaticResource` (`ZiwaPrimaryActionBrush`, `ControlCornerRadius` 6px, `OverlayCornerRadius` 10px, `DialogCornerRadius` 12px, `PillCornerRadius` 999px).
- **فصل العرض عن المنطق:** الـ Code-Behind (`.axaml.cs`) مقتصر على تهيئة `InitializeComponent()` والربط الحركي البصري (`SkiaViewportHost`، تركيز الحقل). كل الحالة والأوامر في ViewModels (`CommunityToolkit.Mvvm`).
- **الوصولية والانتظار الصادق:** كل زر أيقوني يحمل `ToolTip.Tip` و`AutomationProperties.Name`. لا تستخدم مؤشراً مزعجاً لأقل من `1s`؛ فوق `3s` أو الشبكة أظهر حالة صادقة مع زر إلغاء مربوط بـ `CancellationToken`. كل مهمة غير متزامنة تحمل معرّف محاولة يمنع النتائج المتأخرة من تحديث هدف مغلق أو مبدّل.
- **السياقية بدون ضوضاء:** لا أيقونات زخرفية داخل عناوين الأقسام أو المنزلقات؛ عند تحديد ملصق أظهر إجراءات الملصق أولاً واطوِ أدوات الـ AI الفوتوغرافية تلقائياً.
