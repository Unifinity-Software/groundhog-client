import js from '@eslint/js';
import ts from 'typescript-eslint';
import { fileURLToPath } from 'node:url';
export default ts.config({ ignores: ['vscode/test/smoke/*.cjs'] }, js.configs.recommended, ...ts.configs.recommended, {
  files: ['**/*.ts'], rules: { '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }] }
}).map(config => ({ ...config, basePath: fileURLToPath(new URL('../', import.meta.url)) }));
