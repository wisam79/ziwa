#!/usr/bin/env node
/**
 * avalonia-audit.mjs — فاحص آلي ثابت لقواعد Avalonia UI 11.3 في Ziwa Studio
 *
 * الاستخدام:
 *   node scripts/avalonia-audit.mjs
 *   node scripts/avalonia-audit.mjs --strict
 *   node scripts/avalonia-audit.mjs --json
 */
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const APP_DIR = join(ROOT, 'src', 'Ziwa.App');
const VIEWS_DIR = join(APP_DIR, 'Views');
const CONTROLS_DIR = join(APP_DIR, 'Controls');
const TOKENS_PATH = join(APP_DIR, 'Themes', 'Tokens.axaml');
const HEADLESS_TESTS_DIR = join(ROOT, 'tests', 'Ziwa.App.Tests');

const STRICT = process.argv.includes('--strict');
const JSON_OUTPUT = process.argv.includes('--json');

const errors = [];
const warnings = [];

function toRel(absPath) {
  return absPath.slice(ROOT.length + 1).replace(/\\/g, '/');
}

function walkFiles(dir, predicate, results = []) {
  if (!existsSync(dir)) return results;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === '.git') continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      walkFiles(full, predicate, results);
    } else if (entry.isFile() && predicate(full)) {
      results.push(full);
    }
  }
  return results;
}

function lineNumberAt(text, index) {
  if (index <= 0) return 1;
  let line = 1;
  for (let i = 0; i < index && i < text.length; i += 1) {
    if (text.charCodeAt(i) === 10) line += 1;
  }
  return line;
}

function stripXmlCommentsPreservingLines(text) {
  return text.replace(/<!--[\s\S]*?-->/g, (comment) =>
    comment.replace(/[^\r\n]/g, ' '),
  );
}

function isSuppressed(rawLines, lineNum, ruleId) {
  const candidates = [rawLines[lineNum - 1] ?? '', rawLines[lineNum - 2] ?? ''];
  for (const line of candidates) {
    const match = line.match(/avalonia-audit-ignore:\s*([^\s*->]+)/i);
    if (match) {
      const target = match[1].trim();
      if (target === 'all' || target === ruleId) return true;
    }
  }
  return false;
}

function report(severity, ruleId, relPath, line, message, fixHint, rawLines) {
  if (rawLines && isSuppressed(rawLines, line, ruleId)) return;
  const item = { severity, ruleId, file: relPath, line, message, fixHint };
  if (severity === 'error') {
    errors.push(item);
  } else {
    warnings.push(item);
  }
}

/**
 * Quote-aware XML/AXAML tag tokenizer (handles '>' inside Style Selector="..." cleanly).
 */
function tokenizeAxamlTags(text) {
  const tokens = [];
  let i = 0;
  const len = text.length;
  while (i < len) {
    const lt = text.indexOf('<', i);
    if (lt === -1) break;
    if (text.startsWith('<?', lt) || text.startsWith('<!', lt)) {
      const gt = text.indexOf('>', lt + 2);
      i = gt === -1 ? len : gt + 1;
      continue;
    }
    const isClosing = text[lt + 1] === '/';
    let cursor = isClosing ? lt + 2 : lt + 1;
    let nameEnd = cursor;
    while (nameEnd < len && /[A-Za-z0-9_.:-]/.test(text[nameEnd])) {
      nameEnd += 1;
    }
    const tagName = text.slice(cursor, nameEnd);
    if (!tagName) {
      i = lt + 1;
      continue;
    }
    let quote = null;
    cursor = nameEnd;
    while (cursor < len) {
      const ch = text[cursor];
      if (quote) {
        if (ch === quote) quote = null;
      } else if (ch === '"' || ch === "'") {
        quote = ch;
      } else if (ch === '>') {
        break;
      }
      cursor += 1;
    }
    if (cursor >= len) break;
    const rawTag = text.slice(lt, cursor + 1);
    const isSelfClosing = !isClosing && cursor > lt && text[cursor - 1] === '/';
    const attrs = text.slice(nameEnd, isSelfClosing ? cursor - 1 : cursor);
    tokens.push({
      tagName,
      isClosing,
      isSelfClosing,
      attrs,
      start: lt,
      end: cursor + 1,
      rawTag,
    });
    i = cursor + 1;
  }
  return tokens;
}

function getAttr(attrs, name) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const m = attrs.match(new RegExp(`(?:^|\\s)${escaped}\\s*=\\s*"([^"]*)"`));
  return m ? m[1] : null;
}

function checkTokensParity() {
  if (!existsSync(TOKENS_PATH)) {
    report(
      'error',
      'tokens-theme-parity',
      toRel(TOKENS_PATH),
      1,
      'ملف Tokens.axaml غير موجود.',
      'أعد إنشاء src/Ziwa.App/Themes/Tokens.axaml مع قاموسي Light وDark.',
    );
    return { themeKeys: new Set(), lightCount: 0, darkCount: 0 };
  }

  const raw = readFileSync(TOKENS_PATH, 'utf8');
  const rawLines = raw.split(/\r?\n/);
  const stripped = stripXmlCommentsPreservingLines(raw);
  const rel = toRel(TOKENS_PATH);

  const lightMatch = stripped.match(/<ResourceDictionary\s+x:Key="Light"[^>]*>([\s\S]*?)<\/ResourceDictionary>/);
  const darkMatch = stripped.match(/<ResourceDictionary\s+x:Key="Dark"[^>]*>([\s\S]*?)<\/ResourceDictionary>/);

  if (!lightMatch || !darkMatch) {
    report(
      'error',
      'tokens-theme-parity',
      rel,
      1,
      'قاموسا ThemeDictionaries (Light وDark) غير مكتملين في Tokens.axaml.',
      'تأكد من وجود <ResourceDictionary x:Key="Light"> و<ResourceDictionary x:Key="Dark">.',
      rawLines,
    );
    return { themeKeys: new Set(), lightCount: 0, darkCount: 0 };
  }

  const extractKeys = (block, blockStart) => {
    const map = new Map();
    for (const m of block.matchAll(/x:Key="([^"]+)"/g)) {
      map.set(m[1], lineNumberAt(stripped, blockStart + m.index));
    }
    return map;
  };

  const lightKeys = extractKeys(lightMatch[1], lightMatch.index);
  const darkKeys = extractKeys(darkMatch[1], darkMatch.index);

  for (const [k, line] of lightKeys) {
    if (!darkKeys.has(k)) {
      report(
        'error',
        'tokens-theme-parity',
        rel,
        line,
        `المفتاح "${k}" موجود في ثيم Light ومفقود في ثيم Dark.`,
        `أضف x:Key="${k}" إلى قاموس Dark في Tokens.axaml.`,
        rawLines,
      );
    }
  }
  for (const [k, line] of darkKeys) {
    if (!lightKeys.has(k)) {
      report(
        'error',
        'tokens-theme-parity',
        rel,
        line,
        `المفتاح "${k}" موجود في ثيم Dark ومفقود في ثيم Light.`,
        `أضف x:Key="${k}" إلى قاموس Light في Tokens.axaml.`,
        rawLines,
      );
    }
  }

  return {
    themeKeys: new Set([...lightKeys.keys(), ...darkKeys.keys()]),
    lightCount: lightKeys.size,
    darkCount: darkKeys.size,
  };
}

function auditAxamlFiles(axamlFiles, themeKeys) {
  const hexRegex = /(?:^|[\s"'=,(])(#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3}))(?=["'\s,;})]|$)/g;
  const staticResRegex = /\{StaticResource\s+([A-Za-z0-9_]+)\}/g;

  for (const filePath of axamlFiles) {
    const raw = readFileSync(filePath, 'utf8');
    const rawLines = raw.split(/\r?\n/);
    const stripped = stripXmlCommentsPreservingLines(raw);
    const rel = toRel(filePath);
    const isTokens = resolve(filePath) === resolve(TOKENS_PATH);
    const isView = rel.startsWith('src/Ziwa.App/Views/');

    if (!isTokens) {
      for (const m of stripped.matchAll(hexRegex)) {
        const line = lineNumberAt(stripped, m.index + m[0].indexOf('#'));
        report(
          'error',
          'no-raw-hex',
          rel,
          line,
          `لون Hex خام "${m[1]}" خارج Tokens.axaml.`,
          'استبدل اللون بتوكن {DynamicResource Ziwa...Brush} من Tokens.axaml.',
          rawLines,
        );
      }

      for (const m of stripped.matchAll(staticResRegex)) {
        const key = m[1];
        if (themeKeys.has(key)) {
          const line = lineNumberAt(stripped, m.index);
          report(
            'error',
            'no-static-theme-resource',
            rel,
            line,
            `استخدام StaticResource مع مفتاح ثيم "${key}" يجمّد اللون عند تبديل Light/Dark.`,
            `استبدلها بـ {DynamicResource ${key}}.`,
            rawLines,
          );
        }
      }
    }

    if (isView && !/\bx:DataType\s*=/.test(stripped)) {
      report(
        'error',
        'compiled-bindings-view-datatype',
        rel,
        1,
        'ملف View يفتقد التصريح الإلزامي x:DataType للربط المصرّف.',
        'أضف x:DataType="vm:..." على العنصر الجذري في الملف.',
        rawLines,
      );
    }

    for (const m of stripped.matchAll(/ReflectionBinding|x:CompileBindings\s*=\s*"False"/g)) {
      const line = lineNumberAt(stripped, m.index);
      report(
        'error',
        'no-reflection-binding',
        rel,
        line,
        `تم استخدام "${m[0]}" مما يعطّل الربط المصرّف (CompiledBindings).`,
        'استعمل {Binding ...} مع x:DataType أو استنتاج النوع في Avalonia 11.3.',
        rawLines,
      );
    }

    const tokens = tokenizeAxamlTags(stripped);
    const tagStack = [];
    const buttonStack = [];

    for (const tok of tokens) {
      if (!tok.isClosing) {
        const line = lineNumberAt(stripped, tok.start);

        if (tok.tagName === 'DataTemplate' && !/\bx:DataType\s*=/.test(tok.attrs)) {
          report(
            'error',
            'compiled-bindings-datatemplate-datatype',
            rel,
            line,
            '<DataTemplate> يفتقد سمة x:DataType للربط المصرّف.',
            'أضف x:DataType="..." على وسم <DataTemplate>.',
            rawLines,
          );
        }

        if (tok.tagName === 'ListBox') {
          const hasExplicitHeight =
            getAttr(tok.attrs, 'Height') !== null || getAttr(tok.attrs, 'MaxHeight') !== null;
          let constrainedAncestor = null;
          for (let idx = tagStack.length - 1; idx >= 0; idx -= 1) {
            const anc = tagStack[idx];
            if (anc.tagName === 'Grid' || anc.tagName === 'DockPanel') {
              constrainedAncestor = anc.tagName;
              break;
            }
            if (anc.hasExplicitHeight) {
              constrainedAncestor = anc.tagName;
              break;
            }
            if (anc.tagName === 'StackPanel') {
              if (!hasExplicitHeight) {
                report(
                  'warn',
                  'unconstrained-listbox-in-stackpanel',
                  rel,
                  line,
                  '<ListBox> داخل <StackPanel> بلا ارتفاع مقيد (Height/MaxHeight) يعطّل افتراضية VirtualizingStackPanel.',
                  'انقل ListBox إلى صف Grid بارتفاع "*" أو DockPanel، أو حدّد MaxHeight صريحاً.',
                  rawLines,
                );
              }
              break;
            }
          }
          void constrainedAncestor;
        }

        const parentFlow =
          tagStack.length > 0 ? tagStack[tagStack.length - 1].effectiveFlow : 'Unspecified';
        const ownFlow = getAttr(tok.attrs, 'FlowDirection');
        const effectiveFlow = ownFlow ?? parentFlow;

        if (isView && tok.tagName === 'ContextMenu' && parentFlow === 'LeftToRight' && ownFlow !== 'RightToLeft') {
          report(
            'warn',
            'contextmenu-in-ltr-island',
            rel,
            line,
            '<ContextMenu> داخل حاوية FlowDirection="LeftToRight" بدون إعادة ضبط FlowDirection="RightToLeft".',
            'أضف FlowDirection="RightToLeft" على <ContextMenu>.',
            rawLines,
          );
        }

        if (isView && tok.tagName === 'Button') {
          buttonStack.push({
            line,
            attrs: tok.attrs,
            hasContentAttr: getAttr(tok.attrs, 'Content') !== null,
            hasPathIcon: false,
            hasTextLabel: false,
          });
        } else if (buttonStack.length > 0) {
          const curBtn = buttonStack[buttonStack.length - 1];
          if (tok.tagName === 'PathIcon') curBtn.hasPathIcon = true;
          if (tok.tagName === 'TextBlock' || tok.tagName === 'TextBox' || tok.tagName === 'AccessText') {
            curBtn.hasTextLabel = true;
          }
        }

        if (!tok.isSelfClosing) {
          const hasExplicitHeight =
            getAttr(tok.attrs, 'Height') !== null || getAttr(tok.attrs, 'MaxHeight') !== null;
          tagStack.push({ tagName: tok.tagName, effectiveFlow, hasExplicitHeight });
        }
      } else {
        if (isView && tok.tagName === 'Button' && buttonStack.length > 0) {
          const btn = buttonStack.pop();
          if (btn.hasPathIcon && !btn.hasContentAttr && !btn.hasTextLabel) {
            const hasTip = getAttr(btn.attrs, 'ToolTip.Tip') !== null;
            const hasA11y = getAttr(btn.attrs, 'AutomationProperties.Name') !== null;
            if (!hasTip || !hasA11y) {
              const missing = [!hasTip ? 'ToolTip.Tip' : null, !hasA11y ? 'AutomationProperties.Name' : null]
                .filter(Boolean)
                .join(' و');
              report(
                'warn',
                'icon-button-a11y',
                rel,
                btn.line,
                `زر أيقوني يفتقد ${missing}.`,
                'أضف ToolTip.Tip="..." وAutomationProperties.Name="..." بالعربية.',
                rawLines,
              );
            }
          }
        }

        while (tagStack.length > 0) {
          const top = tagStack.pop();
          if (top.tagName === tok.tagName) break;
        }
      }
    }

    if (isView && /ExtendClientAreaToDecorationsHint\s*=\s*"True"/.test(stripped)) {
      const codeBehind = `${filePath}.cs`;
      if (!existsSync(codeBehind) || !/\bOffScreenMargin\b/.test(readFileSync(codeBehind, 'utf8'))) {
        report(
          'error',
          'extended-chrome-offscreen-margin',
          rel,
          1,
          'النافذة تفعّل ExtendClientAreaToDecorationsHint="True" دون تعويض OffScreenMargin في الكود الخلفي.',
          'أضف تعويض Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default في OnPropertyChanged.',
          rawLines,
        );
      }
    }
  }
}

function auditAppCsharpFiles(csFiles) {
  for (const filePath of csFiles) {
    const raw = readFileSync(filePath, 'utf8');
    const rawLines = raw.split(/\r?\n/);
    const rel = toRel(filePath);
    const isControl = rel.startsWith('src/Ziwa.App/Controls/');

    if (isControl && /:\s*TemplatedControl\b/.test(raw)) {
      for (const m of raw.matchAll(/\bDataContext\s*=\s*this\b/g)) {
        report(
          'error',
          'no-templated-datacontext-this',
          rel,
          lineNumberAt(raw, m.index),
          'تعيين DataContext = this داخل TemplatedControl يكسر وراثة سياق البيانات.',
          'احذفه واستخدم {TemplateBinding ...} داخل ControlTheme.',
          rawLines,
        );
      }
    }

    if (isControl) {
      for (const m of raw.matchAll(/\bWriteableBitmap\b/g)) {
        report(
          'error',
          'no-writeablebitmap-in-controls',
          rel,
          lineNumberAt(raw, m.index),
          'استخدام WriteableBitmap داخل عنصر تحكم في Controls/ يخالف مسار GPU السريع.',
          'استخدم ICustomDrawOperation + ISkiaSharpApiLeaseFeature بدل نسخ البكسل على CPU.',
          rawLines,
        );
      }
    }

    if (/\bISkiaSharpApiLeaseFeature\b/.test(raw)) {
      for (const m of raw.matchAll(/\.Lease\s*\(/g)) {
        const stmtStart = Math.max(
          raw.lastIndexOf(';', m.index),
          raw.lastIndexOf('{', m.index),
          raw.lastIndexOf('}', m.index),
        );
        const stmt = raw.slice(stmtStart + 1, m.index);
        if (!/\busing\b/.test(stmt)) {
          report(
            'error',
            'skia-lease-using',
            rel,
            lineNumberAt(raw, m.index),
            'استدعاء .Lease() على ISkiaSharpApiLeaseFeature بدون using.',
            'استخدم `using var lease = leaseFeature.Lease();` لضمان تحرير سطح الرسم فوراً.',
            rawLines,
          );
        }
      }

      if (isControl) {
        const hasSave = /\.Save\s*\(/.test(raw);
        const hasRestore = /\.Restore(?:ToCount)?\s*\(/.test(raw);
        if (!hasSave || !hasRestore) {
          report(
            'warn',
            'skia-canvas-save-restore',
            rel,
            1,
            'رسم Skia عبر ISkiaSharpApiLeaseFeature بدون حفظ/استعادة حالة اللوحة (Save / RestoreToCount).',
            'غلّف عمليات الرسم بـ `int save = canvas.Save(); try { ... } finally { canvas.RestoreToCount(save); }`.',
            rawLines,
          );
        }
      }
    }

    const attachMatch = raw.match(/\boverride\s+void\s+OnAttachedToVisualTree\b/);
    if (attachMatch && !/\boverride\s+void\s+OnDetachedFromVisualTree\b/.test(raw)) {
      report(
        'error',
        'visual-tree-attach-symmetry',
        rel,
        lineNumberAt(raw, attachMatch.index),
        'تجاوز OnAttachedToVisualTree بدون OnDetachedFromVisualTree متناظر قد يسبب تسرب ذاكرة.',
        'أضف override void OnDetachedFromVisualTree لفك الاشتراكات والمؤقتات.',
        rawLines,
      );
    }

    if (isControl) {
      const capMatch = raw.match(/\.Capture\s*\(/);
      if (capMatch && !/\bOnPointerCaptureLost\b|\bPointerCaptureLost\b/.test(raw)) {
        report(
          'warn',
          'pointer-capture-lost-handler',
          rel,
          lineNumberAt(raw, capMatch.index),
          'عنصر التحكم يلتقط المؤشر (.Capture) دون معالجة OnPointerCaptureLost.',
          'تجاوز OnPointerCaptureLost لإعادة ضبط حالة السحب/الفرشاة عند انقطاع الالتقاط.',
          rawLines,
        );
      }
    }
  }
}

function extractMethodBodyAfterIndex(text, startIndex) {
  const openBrace = text.indexOf('{', startIndex);
  if (openBrace === -1) return null;
  let depth = 0;
  let inString = false;
  let inChar = false;
  for (let i = openBrace; i < text.length; i += 1) {
    const ch = text[i];
    const prev = i > 0 ? text[i - 1] : '';
    if (inString) {
      if (ch === '"' && prev !== '\\') inString = false;
      continue;
    }
    if (inChar) {
      if (ch === "'" && prev !== '\\') inChar = false;
      continue;
    }
    if (ch === '"') {
      inString = true;
    } else if (ch === "'") {
      inChar = true;
    } else if (ch === '{') {
      depth += 1;
    } else if (ch === '}') {
      depth -= 1;
      if (depth === 0) {
        return text.slice(openBrace, i + 1);
      }
    }
  }
  return null;
}

function auditHeadlessTests(testFiles) {
  for (const filePath of testFiles) {
    const raw = readFileSync(filePath, 'utf8');
    const rawLines = raw.split(/\r?\n/);
    const rel = toRel(filePath);

    for (const m of raw.matchAll(/\.CaptureRenderedFrame\s*\(/g)) {
      const stmtStart = Math.max(
        raw.lastIndexOf(';', m.index),
        raw.lastIndexOf('{', m.index),
        raw.lastIndexOf('}', m.index),
      );
      const stmt = raw.slice(stmtStart + 1, m.index);
      if (!/\busing\b/.test(stmt) && !/\.Dispose\s*\(/.test(raw)) {
        report(
          'error',
          'headless-frame-disposal',
          rel,
          lineNumberAt(raw, m.index),
          'استدعاء CaptureRenderedFrame() بدون using أو Dispose() يسرّب ذاكرة Skia الأصلية.',
          'استخدم `using var frame = window.CaptureRenderedFrame();`.',
          rawLines,
        );
      }
    }

    const themeAssign = raw.match(/RequestedThemeVariant\s*=/);
    if (themeAssign && !/\bfinally\b/.test(raw)) {
      report(
        'error',
        'headless-theme-isolation',
        rel,
        lineNumberAt(raw, themeAssign.index),
        'تعديل RequestedThemeVariant في اختبار Headless بدون كتلة finally لاستعادة الثيم السابق.',
        'احفظ previousTheme وأعد تعيينه داخل try { ... } finally { app.RequestedThemeVariant = previousTheme; }.',
        rawLines,
      );
    }

    for (const m of raw.matchAll(/\[(Fact|Theory)(?:\s*\([^)]*\))?\]/g)) {
      const body = extractMethodBodyAfterIndex(raw, m.index + m[0].length);
      if (body && (/\bnew\s+(?:MainWindow|Window)\b/.test(body) || /\.Show\s*\(/.test(body))) {
        report(
          'warn',
          'plain-fact-with-window',
          rel,
          lineNumberAt(raw, m.index),
          `اختبار [${m[1]}] ينشئ أو يعرض نافذة Avalonia بدون سمة [Avalonia${m[1]}].`,
          `استبدل [${m[1]}] بـ [Avalonia${m[1]}] لضمان التنفيذ داخل حلقة Dispatcher الخاصة بـ Headless.`,
          rawLines,
        );
      }
    }
  }
}

function main() {
  const axamlFiles = walkFiles(APP_DIR, (p) => p.endsWith('.axaml'));
  const csAppFiles = walkFiles(APP_DIR, (p) => p.endsWith('.cs'));
  const testFiles = walkFiles(HEADLESS_TESTS_DIR, (p) => p.endsWith('.cs'));

  const { themeKeys, lightCount, darkCount } = checkTokensParity();
  auditAxamlFiles(axamlFiles, themeKeys);
  auditAppCsharpFiles(csAppFiles);
  auditHeadlessTests(testFiles);

  const failed = errors.length > 0 || (STRICT && warnings.length > 0);
  const stats = {
    axamlFiles: axamlFiles.length,
    appCsharpFiles: csAppFiles.length,
    headlessTestFiles: testFiles.length,
    themeLightKeys: lightCount,
    themeDarkKeys: darkCount,
    errors: errors.length,
    warnings: warnings.length,
    strict: STRICT,
  };

  if (JSON_OUTPUT) {
    console.log(JSON.stringify({ stats, errors, warnings }, null, 2));
    process.exit(failed ? 1 : 0);
  }

  console.log('🔍 فاحص قواعد Avalonia UI 11.3 (Ziwa avalonia-audit)');
  console.log(
    `  · ملفات مفحوصة: ${stats.axamlFiles} AXAML · ${stats.appCsharpFiles} C# (App) · ${stats.headlessTestFiles} C# (Headless Tests)`,
  );
  console.log(`  · مفاتيح الثيم (Tokens.axaml): Light=${lightCount}, Dark=${darkCount}`);

  if (errors.length > 0) {
    console.error(`\n❌ أخطاء (${errors.length}):`);
    for (const item of errors) {
      console.error(`  - ${item.file}:${item.line} [${item.ruleId}] ${item.message}`);
      console.error(`    ↳ الحل: ${item.fixHint}`);
    }
  }

  if (warnings.length > 0) {
    console.warn(`\n⚠️ تنبيهات (${warnings.length}):`);
    for (const item of warnings) {
      console.warn(`  - ${item.file}:${item.line} [${item.ruleId}] ${item.message}`);
      console.warn(`    ↳ الحل: ${item.fixHint}`);
    }
  }

  if (failed) {
    console.error(
      STRICT
        ? '\n❌ فشل الفاحص في الوضع الصارم (--strict) بسبب وجود أخطاء أو تنبيهات.'
        : '\n❌ فشل الفاحص بسبب وجود أخطاء في قواعد Avalonia.',
    );
    process.exit(1);
  }

  console.log(
    warnings.length === 0
      ? '\n  ✅ جميع قواعد Avalonia UI 11.3 سليمة بلا أخطاء أو تنبيهات.'
      : `\n  ✅ صفر أخطاء حرجة (مع ${warnings.length} تنبيه إرشادي؛ مرّر --strict لتشديدها).`,
  );
}

main();
