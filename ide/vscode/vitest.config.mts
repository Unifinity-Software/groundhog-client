import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';
export default defineConfig({ resolve: { alias: { vscode: fileURLToPath(new URL('./test/vscodeStub.ts', import.meta.url)) } }, test: { include: ['test/**/*.test.ts'], maxWorkers: 2, fileParallelism: true } });
