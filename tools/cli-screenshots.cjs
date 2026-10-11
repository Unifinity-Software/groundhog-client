// Render the CLI's exported Spectre HTML; never connect to a real service or read credentials.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

(async () => {
    const input = path.resolve(process.argv[2] || 'artifacts/screenshots');
    const output = path.resolve(process.argv[3] || 'docs/images');
    fs.mkdirSync(output, { recursive: true });
    const browser = await chromium.launch({ headless: true,
        ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE } : {}) });
    try {
        const page = await browser.newPage({ viewport: { width: 1400, height: 1000 }, deviceScaleFactor: 1.5 });
        for (const name of ['cli-home', 'cli-session']) {
            const content = fs.readFileSync(path.join(input, `${name}.html`), 'utf8');
            await page.setContent(`<!doctype html><meta charset="utf-8"><style>
                body { margin: 0; padding: 24px; background: #111820; color: #e1e7ef; }
                #terminal { display: inline-block; padding: 24px; border: 1px solid #344151; border-radius: 8px; }
                pre { margin: 0; font: 16px/1.5 Consolas, monospace !important; }
                </style><div id="terminal">${content}</div>`);
            await page.locator('#terminal').screenshot({ path: path.join(output, `${name}.png`) });
        }
    } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
