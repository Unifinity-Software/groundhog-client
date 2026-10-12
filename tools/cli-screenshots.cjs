// Render the CLI's exported Spectre HTML as PNGs; never connect to a real service or read credentials.
// Usage: node tools/cli-screenshots.cjs [input-dir] [output-dir]
// The input is written by the CLI tests when GROUNDHOG_SCREENSHOT_DIR is set (see docs/cli.md). Each frame is painted
// cell by cell on a canvas, like a terminal: backgrounds, block elements and box drawing are drawn as shapes so they
// join without the hairline seams that HTML text layout leaves between characters.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const screens = ['cli-boot', 'cli-menu', 'cli-home', 'cli-narrow', 'cli-tunnels', 'cli-tunnel-detail', 'cli-confirm',
    'cli-session', 'cli-diagnostics', 'cli-sign-in'];

// Parses Spectre's ExportHtml output into rows of { ch, fg, bg, bold } cells.
function parse(html) {
    const body = html.replace(/^[\s\S]*?<pre[^>]*>\r?\n?/, '').replace(/<\/pre>[\s\S]*$/, '');
    const unescape = text => text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'")
        .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code))).replace(/&amp;/g, '&');
    const rows = [[]];
    const add = (text, style) => {
        for (const ch of unescape(text)) {
            if (ch === '\r') { continue; }
            if (ch === '\n') { rows.push([]); continue; }
            rows[rows.length - 1].push({ ch, ...style });
        }
    };
    const pattern = /<span style="([^"]*)">([\s\S]*?)<\/span>|([^<]+)/g;
    for (const match of body.matchAll(pattern)) {
        if (match[3] !== undefined) { add(match[3], {}); continue; }
        const style = {};
        for (const rule of match[1].split(';')) {
            const [key, value] = rule.split(':').map(s => s && s.trim());
            if (key === 'color') { style.fg = value; }
            if (key === 'background-color') { style.bg = value; }
            if (key === 'font-weight' && value === 'bold') { style.bold = true; }
        }
        add(match[2], style);
    }
    while (rows.length && rows[rows.length - 1].length === 0) { rows.pop(); }
    return rows;
}

// Runs in the page: paints the rows on a canvas and returns a PNG data URL.
function paint({ rows, scale }) {
    const cw = 9, chh = 19, margin = 18, font = 15;
    const ink = '#0D1821', text = '#D2E0F2';
    const cols = Math.max(...rows.map(r => r.length));
    const canvas = document.createElement('canvas');
    canvas.width = (cols * cw + margin * 2) * scale;
    canvas.height = (rows.length * chh + margin * 2) * scale;
    const ctx = canvas.getContext('2d');
    ctx.scale(scale, scale);
    ctx.fillStyle = ink;
    ctx.beginPath();
    ctx.roundRect(0.5, 0.5, cols * cw + margin * 2 - 1, rows.length * chh + margin * 2 - 1, 10);
    ctx.fill();
    ctx.strokeStyle = '#2F445A';
    ctx.stroke();
    ctx.textBaseline = 'middle';
    // Directions each box-drawing character connects: up, right, down, left; heavy; rounded corner.
    const box = {
        '─': 'rl', '━': 'rlH', '│': 'ud', '┃': 'udH', '╭': 'drR', '╮': 'dlR', '╰': 'urR', '╯': 'ulR',
        '┌': 'dr', '┐': 'dl', '└': 'ur', '┘': 'ul', '┏': 'drH', '┓': 'dlH', '┗': 'urH', '┛': 'ulH',
        '├': 'udr', '┤': 'udl', '┬': 'rld', '┴': 'rlu', '┼': 'udrl',
    };
    const lower = '▁▂▃▄▅▆▇█', left = '▏▎▍▌▋▊▉█';
    rows.forEach((row, y) => row.forEach((cell, x) => {
        const px = margin + x * cw, py = margin + y * chh;
        ctx.fillStyle = cell.bg || ink;
        ctx.fillRect(px, py, cw + 0.5, chh + 0.5);
        const fg = cell.fg || text;
        ctx.fillStyle = fg;
        ctx.strokeStyle = fg;
        const c = cell.ch;
        if (c === ' ') { return; }
        if (c === '▀') { ctx.fillRect(px, py, cw + 0.5, chh / 2 + 0.5); return; }
        if (lower.includes(c)) { const h = chh * (lower.indexOf(c) + 1) / 8; ctx.fillRect(px, py + chh - h, cw + 0.5, h + 0.5); return; }
        if (left.includes(c)) { ctx.fillRect(px, py, cw * (left.indexOf(c) + 1) / 8 + 0.5, chh + 0.5); return; }
        if (box[c]) {
            const spec = box[c], cx = px + cw / 2, cy = py + chh / 2;
            ctx.lineWidth = spec.includes('H') ? 2.4 : 1.2;
            ctx.lineCap = 'butt';
            ctx.beginPath();
            if (spec.includes('R')) {
                const r = 4, vertical = spec.includes('u') ? py : py + chh, horizontal = spec.includes('l') ? px : px + cw;
                ctx.moveTo(cx, vertical);
                ctx.arcTo(cx, cy, horizontal, cy, r);
                ctx.lineTo(horizontal, cy);
            } else {
                if (spec.includes('u')) { ctx.moveTo(cx, py); ctx.lineTo(cx, cy); }
                if (spec.includes('d')) { ctx.moveTo(cx, cy); ctx.lineTo(cx, py + chh); }
                if (spec.includes('l')) { ctx.moveTo(px, cy); ctx.lineTo(cx, cy); }
                if (spec.includes('r')) { ctx.moveTo(cx, cy); ctx.lineTo(px + cw + 0.5, cy); }
                // Square corners and joints: extend through the centre so the stroke has no notch.
                if (spec.length > 1 && !/^(rl|ud)H?$/.test(spec)) {
                    const half = ctx.lineWidth / 2;
                    ctx.moveTo(cx - half, cy); ctx.lineTo(cx + half, cy);
                }
            }
            ctx.stroke();
            return;
        }
        ctx.font = `${cell.bold ? 'bold ' : ''}${font}px 'DejaVu Sans Mono', monospace`;
        ctx.fillText(c, px, py + chh / 2 + 1);
    }));
    return canvas.toDataURL('image/png');
}

(async () => {
    const input = path.resolve(process.argv[2] || 'artifacts/screenshots');
    const output = path.resolve(process.argv[3] || 'docs/images');
    fs.mkdirSync(output, { recursive: true });
    const browser = await chromium.launch({ headless: true,
        ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE } : {}) });
    try {
        const page = await browser.newPage();
        await page.setContent('<!doctype html><meta charset="utf-8"><body>');
        for (const name of screens) {
            const rows = parse(fs.readFileSync(path.join(input, `${name}.html`), 'utf8'));
            const data = await page.evaluate(paint, { rows, scale: 2 });
            fs.writeFileSync(path.join(output, `${name}.png`), Buffer.from(data.split(',')[1], 'base64'));
        }
    } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
