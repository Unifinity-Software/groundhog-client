import js from '@eslint/js';
import ts from 'typescript-eslint';
export default ts.config({ ignores: ['test/smoke/*.cjs'] }, js.configs.recommended, ...ts.configs.recommended, {
  files: ['**/*.ts'], rules: { '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }] }
});
