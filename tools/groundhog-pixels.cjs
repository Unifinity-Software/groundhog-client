// Generates src/Groundhog.Cli/Views/GroundhogPixels.cs, the CLI's pixel-art groundhog head, from the brand app icon.
// Usage: node tools/groundhog-pixels.cjs [icon.png] [size]
// Reads an 8-bit RGBA, non-interlaced PNG with Node's zlib only, maps every source pixel to the nearest brand colour, takes
// each size×size cell's most common colour and drops the dark icon background that touches the edge, so only the head remains.
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const root = path.resolve(__dirname, '..');
const input = path.resolve(process.argv[2] || path.join(root, 'assets/brand/app-icons/icon-192.png'));
const size = Number(process.argv[3] || 32);
const output = path.join(root, 'src/Groundhog.Cli/Views/GroundhogPixels.cs');

// Brand palette: index letters are what the generated rows contain.
const palette = [
    { key: 'n', name: 'Ink', rgb: [0x0d, 0x18, 0x21] },
    { key: 'c', name: 'Signal', rgb: [0x2e, 0xe5, 0xf1] },
    { key: 'w', name: 'White', rgb: [0xff, 0xff, 0xff] },
];

function decodePng(file) {
    const bytes = fs.readFileSync(file);
    if (bytes.readUInt32BE(0) !== 0x89504e47) { throw new Error('Not a PNG file.'); }
    let offset = 8, width = 0, height = 0;
    const data = [];
    while (offset < bytes.length) {
        const length = bytes.readUInt32BE(offset);
        const type = bytes.toString('ascii', offset + 4, offset + 8);
        const chunk = bytes.subarray(offset + 8, offset + 8 + length);
        if (type === 'IHDR') {
            width = chunk.readUInt32BE(0); height = chunk.readUInt32BE(4);
            if (chunk[8] !== 8 || chunk[9] !== 6 || chunk[12] !== 0) { throw new Error('Expected 8-bit RGBA, non-interlaced.'); }
        } else if (type === 'IDAT') { data.push(chunk); }
        offset += 12 + length;
    }
    const raw = zlib.inflateSync(Buffer.concat(data));
    const stride = width * 4;
    const pixels = Buffer.alloc(stride * height);
    for (let y = 0; y < height; y++) {
        const filter = raw[y * (stride + 1)];
        for (let x = 0; x < stride; x++) {
            const value = raw[y * (stride + 1) + 1 + x];
            const left = x >= 4 ? pixels[y * stride + x - 4] : 0;
            const up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
            const upLeft = x >= 4 && y > 0 ? pixels[(y - 1) * stride + x - 4] : 0;
            let predicted = 0;
            if (filter === 1) { predicted = left; }
            else if (filter === 2) { predicted = up; }
            else if (filter === 3) { predicted = (left + up) >> 1; }
            else if (filter === 4) {
                const p = left + up - upLeft, pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - upLeft);
                predicted = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
            }
            pixels[y * stride + x] = (value + predicted) & 0xff;
        }
    }
    return { width, height, pixels };
}

function nearest(colour) {
    return palette.reduce((best, entry) => {
        const distance = entry.rgb.reduce((sum, v, i) => sum + (v - colour[i]) ** 2, 0);
        return distance < best.distance ? { key: entry.key, distance } : best;
    }, { key: '.', distance: Infinity }).key;
}

const image = decodePng(input);
const cell = image.width / size;
const grid = [];
for (let gy = 0; gy < size; gy++) {
    const row = [];
    for (let gx = 0; gx < size; gx++) {
        // Majority vote of the source pixels' nearest brand colours keeps edges crisp (averaging invents in-between colours).
        const votes = {};
        for (let y = Math.floor(gy * cell); y < Math.floor((gy + 1) * cell); y++) {
            for (let x = Math.floor(gx * cell); x < Math.floor((gx + 1) * cell); x++) {
                const i = (y * image.width + x) * 4;
                const key = image.pixels[i + 3] < 128 ? '.' : nearest([image.pixels[i], image.pixels[i + 1], image.pixels[i + 2]]);
                // Ink outlines are thin; weigh them up so the eye, nose and smile survive the reduction.
                votes[key] = (votes[key] || 0) + (key === 'n' ? 2 : 1);
            }
        }
        row.push(Object.entries(votes).sort((a, b) => b[1] - a[1])[0][0]);
    }
    grid.push(row);
}

// The icon's dark tile is background: clear every ink cell connected to the edge.
const stack = [];
for (let i = 0; i < size; i++) { stack.push([i, 0], [i, size - 1], [0, i], [size - 1, i]); }
while (stack.length) {
    const [x, y] = stack.pop();
    if (x < 0 || y < 0 || x >= size || y >= size || grid[y][x] !== 'n') { continue; }
    grid[y][x] = '.';
    stack.push([x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]);
}

// Crop to the head.
const rows = grid.map(r => r.join(''));
const used = rows.map((r, y) => [r, y]).filter(([r]) => /[^.]/.test(r)).map(([, y]) => y);
const columns = [...Array(size).keys()].filter(x => rows.some(r => r[x] !== '.'));
const cropped = rows.slice(used[0], used[used.length - 1] + 1).map(r => r.slice(columns[0], columns[columns.length - 1] + 1));

const source = `// <auto-generated>
// Generated by tools/groundhog-pixels.cjs from ${path.relative(root, input).replaceAll('\\', '/')} (${size}×${size}). Do not edit by hand.
// </auto-generated>
namespace Groundhog.Cli.Views;

/// <summary>The groundhog head as a pixel map: n ink, c signal cyan, w white, . transparent.</summary>
internal static class GroundhogPixels
{
    internal static readonly string[] Rows =
    [
${cropped.map(r => `        "${r}",`).join('\n')}
    ];
}
`;
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, source);
console.log(cropped.join('\n'));
