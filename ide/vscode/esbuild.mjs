import { build, context } from 'esbuild';
const options = { entryPoints: ['src/extension.ts'], bundle: true, platform: 'node', format: 'cjs', target: 'node20', external: ['vscode'], outfile: 'dist/extension.cjs', sourcemap: false, minify: true };
if (process.argv.includes('--watch')) await (await context(options)).watch();
else await build(options);
