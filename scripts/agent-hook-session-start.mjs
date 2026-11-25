import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Ziwa Studio — Antigravity SessionStart Hook
 * يذكّر الوكيل بمؤشر الاستئناف الحي وحالة الـ fragments عند بدء الجلسة.
 */
function main() {
  try {
    const cwd = process.cwd();
    const projectRoot = existsSync(join(cwd, 'changelog')) ? cwd : join(cwd, '..');
    const fragDir = join(projectRoot, 'changelog', 'fragments');
    const planPath = join(projectRoot, 'docs', 'DEVELOPMENT_PLAN.md');

    const parts = [
      '🛡️ بيئة Ziwa Studio (.NET 10 LTS + Avalonia UI 11 + SkiaSharp) جاهزة. اقرأ docs/DEVELOPMENT_PLAN.md أولاً؛ التزم بمبدأ Preview==Export==Print وبوابة node scripts/docs-gate.mjs --strict-refs.',
    ];

    if (existsSync(planPath)) {
      const plan = readFileSync(planPath, 'utf8');
      const nextMatch = plan.match(/\*\*المهمة التالية (?:المباشرة|الموصى بها)\*\*\s*\|\s*(.+?)\s*\|/);
      if (nextMatch) {
        parts.push(`الخطوة النشطة الآن: ${nextMatch[1]}.`);
      }
    }

    if (existsSync(fragDir)) {
      const frags = readdirSync(fragDir).filter((f) => f.endsWith('.md') && !f.startsWith('_'));
      parts.push(`يوجد ${frags.length} fragment(s) في changelog/fragments/.`);
    }

    process.stdout.write(
      JSON.stringify({
        injectSteps: [
          {
            systemMessage: {
              systemMessage: parts.join(' '),
            },
          },
        ],
      }),
    );
  } catch {
    process.stdout.write(JSON.stringify({ injectSteps: [] }));
  }
}

main();
