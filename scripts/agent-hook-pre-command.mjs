import { readFileSync } from 'node:fs';

/**
 * Ziwa Studio — Antigravity PreToolUse Hook
 * يمنع الأخطاء البنيوية القاتلة دون تقييد سير التطوير الطبيعي:
 * 1. حظر `cd` قطعياً (استخدم Cwd أو المسارات المطلقة).
 * 2. حظر تجاوز خطافات التحقق (--no-verify / -n) في git commit وgit push.
 */
function main() {
  try {
    const rawInput = readFileSync(0, 'utf-8');
    if (!rawInput.trim()) {
      process.stdout.write(JSON.stringify({ decision: 'allow' }));
      return;
    }

    const payload = JSON.parse(rawInput);
    const toolName = payload?.toolCall?.name;
    const cmd = payload?.toolCall?.args?.CommandLine || '';

    if (toolName === 'run_command' && typeof cmd === 'string') {
      const cdRegex = /(?:^|[;&|\r\n]\s*)\bcd(?:\.exe)?(?:\s+|$)/i;
      if (cdRegex.test(cmd)) {
        process.stdout.write(
          JSON.stringify({
            decision: 'deny',
            reason:
              '❌ أمر "cd" محظور في Ziwa Studio وفق (.agents/AGENTS.md). استخدم معامل Cwd أو المسارات الكاملة.',
          }),
        );
        return;
      }

      const noVerifyRegex = /\bgit\s+(?:commit|push)\b.*(?:\s--no-verify|\s-n\b)/i;
      if (noVerifyRegex.test(cmd)) {
        process.stdout.write(
          JSON.stringify({
            decision: 'deny',
            reason:
              '❌ يُمنع استخدام --no-verify أو -n لتجاوز فحوصات الجودة وبوابة التوثيق في Ziwa Studio.',
          }),
        );
        return;
      }
    }

    process.stdout.write(JSON.stringify({ decision: 'allow' }));
  } catch {
    process.stdout.write(JSON.stringify({ decision: 'allow' }));
  }
}

main();
