#!/usr/bin/env node
/**
 * release.mjs — إدارة إصدار Ziwa Studio، فحص تطابق النسخة، توليد ziwa-checksums.txt، وتجميع CHANGELOG.md
 *
 * الاستخدام:
 *   node scripts/release.mjs --check [--tag v1.0.0]
 *   node scripts/release.mjs --changelog
 *   node scripts/release.mjs --checksums <distDir>
 */
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function getArgValue(flag) {
  const idx = process.argv.indexOf(flag);
  if (idx >= 0 && idx + 1 < process.argv.length) {
    return process.argv[idx + 1];
  }
  return null;
}

function readDirectoryBuildPropsVersion() {
  const propsPath = join(ROOT, 'Directory.Build.props');
  const content = readFileSync(propsPath, 'utf8');
  const match = content.match(/<Version>\s*([^<\s]+)\s*<\/Version>/);
  if (!match) {
    throw new Error('تعذر قراءة <Version> من Directory.Build.props');
  }
  return match[1].trim();
}

function readInstallerVersion() {
  const issPath = join(ROOT, 'installer', 'ZiwaStudio.iss');
  if (!existsSync(issPath)) {
    throw new Error('ملف المثبّت installer/ZiwaStudio.iss غير موجود.');
  }
  const content = readFileSync(issPath, 'utf8');
  const match = content.match(/^#define\s+MyAppVersion\s+"([^"]+)"/m);
  if (!match) {
    throw new Error('تعذر قراءة #define MyAppVersion من installer/ZiwaStudio.iss');
  }
  return match[1].trim();
}

function checkVersionConsistency() {
  const propsVersion = readDirectoryBuildPropsVersion();
  const issVersion = readInstallerVersion();
  if (propsVersion !== issVersion) {
    throw new Error(
      `تعارض أرقام الإصدار: Directory.Build.props=${propsVersion} بينما installer/ZiwaStudio.iss=${issVersion}`,
    );
  }

  const tagArg = getArgValue('--tag');
  if (tagArg) {
    const cleanTag = tagArg.replace(/^v/i, '').trim();
    if (cleanTag !== propsVersion) {
      throw new Error(`وسم الإصدار (${tagArg}) لا يطابق Directory.Build.props (${propsVersion})`);
    }
  }

  console.log(`✅ تطابق الإصدار سليم: v${propsVersion} (Directory.Build.props + installer/ZiwaStudio.iss)`);
}

function assembleChangelog() {
  const version = readDirectoryBuildPropsVersion();
  const fragDir = join(ROOT, 'changelog', 'fragments');
  const files = readdirSync(fragDir)
    .filter((f) => f.endsWith('.md') && !f.startsWith('_'))
    .sort()
    .reverse();

  const entries = [];
  for (const file of files) {
    const raw = readFileSync(join(fragDir, file), 'utf8');
    const fmMatch = raw.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
    if (!fmMatch) continue;
    const fm = fmMatch[1];
    const body = fmMatch[2].trim().replace(/\r?\n+/g, ' ');
    const type = (fm.match(/^type:\s*([^\s#]+)/m)?.[1] ?? 'feat').trim();
    const scope = (fm.match(/^scope:\s*([^\s#]+)/m)?.[1] ?? 'core').trim();
    const datePrefix = file.slice(0, 8);
    const formattedDate = /^\d{8}$/.test(datePrefix)
      ? `${datePrefix.slice(0, 4)}-${datePrefix.slice(4, 6)}-${datePrefix.slice(6, 8)}`
      : 'unknown';
    entries.push(`- **[${type}/${scope}]** (${formattedDate}) ${body}`);
  }

  const today = new Date().toISOString().slice(0, 10);
  const output = [
    '# سجل التغييرات — Ziwa Studio (CHANGELOG)',
    '',
    '> **تنبيه:** يُولَّد هذا الملف آلياً من `changelog/fragments/*.md` عبر `node scripts/release.mjs --changelog` (انظر [`changelog/README.md`](changelog/README.md)).',
    '',
    `## [v${version}] — ${today}`,
    '',
    ...entries,
    '',
  ].join('\n');

  writeFileSync(join(ROOT, 'CHANGELOG.md'), output, 'utf8');
  console.log(`✅ تم تجميع ${entries.length} fragment في CHANGELOG.md للإصدار v${version}`);
}

function generateChecksums(distDirArg) {
  const distDir = resolve(ROOT, distDirArg);
  if (!existsSync(distDir) || !statSync(distDir).isDirectory()) {
    throw new Error(`مجلد المخرجات غير موجود: ${distDir}`);
  }

  const artifacts = readdirSync(distDir)
    .filter((name) => /\.(exe|msi|zip)$/i.test(name))
    .sort();

  if (artifacts.length === 0) {
    throw new Error(`لا توجد حزم (.exe/.msi/.zip) داخل ${distDir}`);
  }

  const lines = [];
  for (const name of artifacts) {
    const data = readFileSync(join(distDir, name));
    const sha256 = createHash('sha256').update(data).digest('hex');
    lines.push(`${sha256}  ${name}`);
  }

  const outPath = join(distDir, 'ziwa-checksums.txt');
  writeFileSync(outPath, `${lines.join('\n')}\n`, 'ascii');
  console.log(`✅ تم توليد ${outPath} (${lines.length} أصول)`);
}

function main() {
  const doCheck = process.argv.includes('--check');
  const doChangelog = process.argv.includes('--changelog');
  const checksumsDir = getArgValue('--checksums');

  if (!doCheck && !doChangelog && !checksumsDir) {
    console.error('الاستخدام: node scripts/release.mjs [--check [--tag v1.0.0]] [--changelog] [--checksums <dir>]');
    process.exit(1);
  }

  if (doCheck) checkVersionConsistency();
  if (doChangelog) assembleChangelog();
  if (checksumsDir) generateChecksums(checksumsDir);
}

main();
