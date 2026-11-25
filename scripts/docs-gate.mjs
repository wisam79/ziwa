#!/usr/bin/env node
/**
 * docs-gate.mjs — بوابة التوثيق والمراجع الرشيقة لمشروع Ziwa Studio
 *
 * الاستخدام:
 *   node scripts/docs-gate.mjs
 *   node scripts/docs-gate.mjs --strict-refs
 */
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const STRICT_REFS = process.argv.includes('--strict-refs');
const ALLOWED_TYPES = new Set(['feat', 'fix', 'perf', 'refactor', 'docs', 'security', 'removed']);

const errors = [];

function walkMarkdownFiles(dir, results = []) {
  if (!existsSync(dir)) return results;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === '.git') continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      walkMarkdownFiles(full, results);
    } else if (entry.isFile() && entry.name.endsWith('.md')) {
      results.push(full);
    }
  }
  return results;
}

function validateFragments() {
  const fragDir = join(ROOT, 'changelog', 'fragments');
  if (!existsSync(fragDir)) {
    errors.push('مجلد changelog/fragments غير موجود.');
    return 0;
  }

  const files = readdirSync(fragDir).filter((f) => f.endsWith('.md') && !f.startsWith('_'));
  for (const file of files) {
    const full = join(fragDir, file);
    const content = readFileSync(full, 'utf8');
    const fmMatch = content.match(/^---\r?\n([\s\S]*?)\r?\n---/);
    if (!fmMatch) {
      errors.push(`changelog/fragments/${file}: يفتقد ترويسة YAML.`);
      continue;
    }

    const fm = fmMatch[1];
    const typeMatch = fm.match(/^type:\s*([^\s#]+)/m);
    if (!typeMatch || !ALLOWED_TYPES.has(typeMatch[1])) {
      errors.push(
        `changelog/fragments/${file}: نوع غير صالح (${typeMatch?.[1] ?? 'مفقود'}). المسموح: ${[...ALLOWED_TYPES].join(', ')}`,
      );
    }

    const relMatches = [...fm.matchAll(/^\s*-\s+([^\s:#]+)(?::\d+)?/gm)].map((m) => m[1]);
    if (relMatches.length === 0) {
      errors.push(`changelog/fragments/${file}: حقل related_files فارغ.`);
      continue;
    }

    for (const relPath of relMatches) {
      const target = join(ROOT, relPath);
      if (!existsSync(target)) {
        errors.push(`changelog/fragments/${file}: المسار غير موجود فعلياً: ${relPath}`);
      }
    }
  }
  return files.length;
}

function validateMarkdownLinks() {
  const targets = [
    join(ROOT, 'README.md'),
    ...walkMarkdownFiles(join(ROOT, 'docs')),
    ...walkMarkdownFiles(join(ROOT, '.agents')),
    join(ROOT, 'changelog', 'README.md'),
  ].filter((p) => existsSync(p));

  const linkRe = /\[[^\]]+\]\(([^)]+)\)/g;
  let checkedCount = 0;

  for (const filePath of targets) {
    const content = readFileSync(filePath, 'utf8');
    const dir = dirname(filePath);

    for (const match of content.matchAll(linkRe)) {
      const rawTarget = match[1].trim();
      if (
        !rawTarget ||
        rawTarget.startsWith('http://') ||
        rawTarget.startsWith('https://') ||
        rawTarget.startsWith('#') ||
        rawTarget.startsWith('mailto:')
      ) {
        continue;
      }

      const cleanTarget = rawTarget.split('#')[0];
      if (!cleanTarget) continue;

      const resolved = cleanTarget.startsWith('/')
        ? join(ROOT, cleanTarget.slice(1))
        : resolve(dir, cleanTarget);

      checkedCount += 1;
      if (!existsSync(resolved) || (!statSync(resolved).isFile() && !statSync(resolved).isDirectory())) {
        const relFile = filePath.slice(ROOT.length + 1).replace(/\\/g, '/');
        errors.push(`${relFile}: رابط مكسور -> ${rawTarget}`);
      }
    }
  }

  return checkedCount;
}

function main() {
  const fragCount = validateFragments();
  const linkCount = STRICT_REFS ? validateMarkdownLinks() : 0;

  console.log('📚 بوابة التوثيق (Ziwa docs-gate)');
  console.log(`  · fragments مفحوصة: ${fragCount}`);
  if (STRICT_REFS) {
    console.log(`  · روابط داخلية متحقّق منها (--strict-refs): ${linkCount}`);
  }

  if (errors.length > 0) {
    console.error('\n❌ أخطاء بوابة التوثيق:');
    for (const err of errors) {
      console.error(`  - ${err}`);
    }
    process.exit(1);
  }

  console.log('  ✅ كل المراجع والـ fragments سليمة — البوابة ناجحة.');
}

main();
