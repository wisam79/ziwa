#!/usr/bin/env node
/**
 * installer-policy-check.mjs — التحقق من سياسة سلامة بيانات المستخدم في المثبّت (ZiwaStudio.iss)
 *
 * يضمن:
 * 1. وجود AppId ثابت للترقية النظيفة.
 * 2. تسجيل امتداد .ziwa.
 * 3. عدم وجود أي حذف لمجلدات بيانات المستخدم ({localappdata}\ZiwaStudio, {userdocs}, projects.db, *.ziwa).
 */
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ISS_PATH = join(ROOT, 'installer', 'ZiwaStudio.iss');

if (!existsSync(ISS_PATH)) {
  console.error('❌ installer/ZiwaStudio.iss غير موجود.');
  process.exit(1);
}

const content = readFileSync(ISS_PATH, 'utf8');
const errors = [];

if (!/^AppId=\{\{[0-9A-Fa-f-]+\}/m.test(content)) {
  errors.push('يجب تعريف AppId ثابت بصيغة GUID داخل [Setup].');
}

if (!content.includes('.ziwa') || !content.includes('ZiwaStudio.Project')) {
  errors.push('يجب تسجيل اقتران امتداد .ziwa داخل [Registry].');
}

if (content.includes('[UninstallDelete]')) {
  const section = content.split('[UninstallDelete]')[1]?.split(/^\[/m)[0] ?? '';
  const forbiddenTargets = ['{localappdata}\\ZiwaStudio', '{userdocs}', '{appdata}', 'projects.db', '*.ziwa'];
  for (const forbidden of forbiddenTargets) {
    if (section.toLowerCase().includes(forbidden.toLowerCase())) {
      errors.push(`ممنوع حذف بيانات المستخدم في [UninstallDelete]: ${forbidden}`);
    }
  }
}

for (const line of content.split(/\r?\n/)) {
  const lower = line.toLowerCase();
  if ((lower.includes('rmdir') || lower.includes('delete') || lower.includes('del ')) &&
      (lower.includes('ziwastudio\\projects') || lower.includes('projects.db') || lower.includes('{userdocs}'))) {
    errors.push(`تعليمة حذف خطرة على بيانات المستخدم: ${line.trim()}`);
  }
}

if (errors.length > 0) {
  console.error('❌ فشل فحص سياسة المثبّت:');
  for (const err of errors) {
    console.error(`  - ${err}`);
  }
  process.exit(1);
}

console.log('✅ سياسة المثبّت سليمة: الترقية وإلغاء التثبيت تحفظان مشاريع وبيانات المستخدم بالكامل.');
