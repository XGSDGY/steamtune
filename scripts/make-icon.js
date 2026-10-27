'use strict';

/**
 * 生成 build/icon.ico（256×256，PNG 载荷）。
 * 不依赖任何图形库：手写 PNG 编码 + ICO 容器。
 * 用法：node scripts/make-icon.js
 */
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const SIZE = 256;

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (const byte of buf) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crc]);
}

function png(size, pixel) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  let offset = 0;
  for (let y = 0; y < size; y += 1) {
    raw[offset] = 0; // filter: none
    offset += 1;
    for (let x = 0; x < size; x += 1) {
      const [r, g, b, a] = pixel(x, y);
      raw[offset] = r;
      raw[offset + 1] = g;
      raw[offset + 2] = b;
      raw[offset + 3] = a;
      offset += 4;
    }
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function smoothstep(edge0, edge1, x) {
  const t = Math.min(1, Math.max(0, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
}

function lerp(a, b, t) {
  return a + (b - a) * t;
}

function mix(c1, c2, t) {
  return [lerp(c1[0], c2[0], t), lerp(c1[1], c2[1], t), lerp(c1[2], c2[2], t)];
}

const VIOLET = [124, 156, 255];
const TEAL = [72, 224, 200];
const INK = [10, 14, 22];

/** 圆角矩形 SDF */
function roundedRectSdf(x, y, cx, cy, halfW, halfH, radius) {
  const dx = Math.abs(x - cx) - (halfW - radius);
  const dy = Math.abs(y - cy) - (halfH - radius);
  const ax = Math.max(dx, 0);
  const ay = Math.max(dy, 0);
  return Math.sqrt(ax * ax + ay * ay) + Math.min(Math.max(dx, dy), 0) - radius;
}

/** 胶囊（用于符干） */
function capsule(x, y, x0, y0, x1, y1, radius) {
  const vx = x1 - x0;
  const vy = y1 - y0;
  const wx = x - x0;
  const wy = y - y0;
  const t = Math.min(1, Math.max(0, (wx * vx + wy * vy) / (vx * vx + vy * vy)));
  const px = x0 + vx * t;
  const py = y0 + vy * t;
  return Math.hypot(x - px, y - py) - radius;
}

function ellipse(x, y, cx, cy, rx, ry, angle) {
  const cos = Math.cos(angle);
  const sin = Math.sin(angle);
  const dx = x - cx;
  const dy = y - cy;
  const ex = dx * cos + dy * sin;
  const ey = -dx * sin + dy * cos;
  const q = Math.sqrt((ex * ex) / (rx * rx) + (ey * ey) / (ry * ry));
  // 乘回半径，得到近似距离，边界才不会被羽化成一团
  return (q - 1) * Math.min(rx, ry);
}

function pixel(x, y) {
  const cx = SIZE / 2;
  const cy = SIZE / 2;

  // 背景：圆角方块 + 对角渐变
  const bgSdf = roundedRectSdf(x, y, cx, cy, 120, 120, 56);
  const bgAlpha = 1 - smoothstep(-1.5, 1.5, bgSdf);
  if (bgAlpha <= 0) return [0, 0, 0, 0];

  const t = Math.min(1, Math.max(0, (x + y) / (SIZE * 1.55)));
  let color = mix(mix(VIOLET, [90, 130, 240], t), TEAL, Math.max(0, t - 0.55) / 0.45);

  // 高光
  const glow = Math.max(0, 1 - Math.hypot(x - 74, y - 62) / 150);
  color = mix(color, [255, 255, 255], glow * 0.16);

  // 音符：两个符头 + 两根符干 + 横梁，小尺寸下也能认出来
  const headA = ellipse(x, y, 92, 176, 27, 20, -0.3);
  const headB = ellipse(x, y, 172, 156, 27, 20, -0.3);
  const stemA = capsule(x, y, 118, 172, 118, 88, 9);
  const stemB = capsule(x, y, 198, 152, 198, 62, 9);
  const beam = capsule(x, y, 118, 90, 198, 66, 10);

  const ink = Math.min(headA, headB, stemA, stemB, beam);
  const inkAlpha = 1 - smoothstep(-1.2, 1.2, ink);
  if (inkAlpha > 0) color = mix(color, INK, inkAlpha * 0.92);

  // 内描边，让图标在小尺寸下更清晰
  const ring = Math.abs(bgSdf + 3) - 0.8;
  const ringAlpha = 1 - smoothstep(-0.8, 0.8, ring);
  if (ringAlpha > 0) color = mix(color, [255, 255, 255], ringAlpha * 0.22);

  return [Math.round(color[0]), Math.round(color[1]), Math.round(color[2]), Math.round(bgAlpha * 255)];
}

function ico(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(images.length, 4);

  const entries = [];
  let offset = 6 + images.length * 16;
  const blobs = [];
  for (const img of images) {
    const entry = Buffer.alloc(16);
    entry[0] = img.size >= 256 ? 0 : img.size;
    entry[1] = img.size >= 256 ? 0 : img.size;
    entry[2] = 0;
    entry[3] = 0;
    entry.writeUInt16LE(1, 4); // planes
    entry.writeUInt16LE(32, 6); // bpp
    entry.writeUInt32LE(img.data.length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += img.data.length;
    entries.push(entry);
    blobs.push(img.data);
  }
  return Buffer.concat([header, ...entries, ...blobs]);
}

const outDir = path.join(__dirname, '..', 'build');
fs.mkdirSync(outDir, { recursive: true });

const sizes = [256, 64, 32];
const images = sizes.map((size) => ({
  size,
  data: png(size, (x, y) => pixel(Math.floor((x / size) * SIZE), Math.floor((y / size) * SIZE))),
}));

const file = path.join(outDir, 'icon.ico');
fs.writeFileSync(file, ico(images));

// electron-builder 也能直接吃 PNG，留一份 256×256 的原始图
const pngFile = path.join(outDir, 'icon.png');
fs.writeFileSync(pngFile, images[0].data);

console.log(`已生成 ${file}（${sizes.join('/')} px，${fs.statSync(file).size} 字节）`);
console.log(`已生成 ${pngFile}（${images[0].data.length} 字节）`);
