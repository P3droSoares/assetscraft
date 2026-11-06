#!/usr/bin/env node
// AssetsCraft sprite tool — builds sprites / sprite sheets from a JSON source, separately from the app.
//
//   node tools/sprite.mjs render sprites/<nome>.sprite.json
//        → nova versão: out/<nome>-v1.png (folha) + out/<nome>-v1.json (metadados estilo Aseprite)
//          + out/gif/<nome>-v1-<animação>.gif + out/.preview/<nome>-v1.png (ampliada, para conferir)
//   node tools/sprite.mjs preview <arquivo.png> [--zoom 8]      → out/.preview/<arquivo>.png ampliado
//   node tools/sprite.mjs inspect <arquivo.png> [--cell 16x16] [--to sprites/x.sprite.json] [--max-colors 48]
//        → paleta + grade de pixels (texto) de uma imagem existente, para editar ou reaproveitar
//   node tools/sprite.mjs palettes                               → paletas prontas (pico8, sweetie16, …)
//
// Versions are never overwritten: every render writes the next -vN. The app lists out/<nome>-vN.png.
import * as fs from "node:fs/promises";
import * as fss from "node:fs";
import * as path from "node:path";
import pngjs from "pngjs";
import * as gifenc from "gifenc";

const { PNG } = pngjs;
const { GIFEncoder, quantize, applyPalette } = gifenc.GIFEncoder ? gifenc : gifenc.default;

// ---------------------------------------------------------------- palettes
const KEYS = "0123456789abcdefghijklmnopqrstuv";
const PALETTES = {
  pico8: ["000000", "1d2b53", "7e2553", "008751", "ab5236", "5f574f", "c2c3c7", "fff1e8",
    "ff004d", "ffa300", "ffec27", "00e436", "29adff", "83769c", "ff77a8", "ffccaa"],
  sweetie16: ["1a1c2c", "5d275d", "b13e53", "ef7d57", "ffcd75", "a7f070", "38b764", "257179",
    "29366f", "3b5dc9", "41a6f6", "73eff7", "f4f4f4", "94b0c2", "566c86", "333c57"],
  gameboy: ["0f380f", "306230", "8bac0f", "9bbc0f"],
  endesga32: ["be4a2f", "d77643", "ead4aa", "e4a672", "b86f50", "733e39", "3e2731", "a22633",
    "e43b44", "f77622", "feae34", "fee761", "63c74d", "3e8948", "265c42", "193c3e",
    "124e89", "0099db", "2ce8f5", "ffffff", "c0cbdc", "8b9bb4", "5a6988", "3a4466",
    "262b44", "181425", "ff0044", "68386c", "b55088", "f6757a", "e8b796", "c28569"],
};

// ---------------------------------------------------------------- game palette (set by the app)
/** Fixed palette of the linked game, from .assetscraft/palette.json: { name, colors: ["#rrggbb", …] } or null. */
let gamePaletteCache;
function gamePalette() {
  if (gamePaletteCache !== undefined) return gamePaletteCache;
  try {
    const p = JSON.parse(fss.readFileSync(path.resolve(".assetscraft", "palette.json"), "utf8"));
    const colors = (p.colors ?? []).map((c) => String(c).replace(/^#/, "").toLowerCase()).filter((c) => /^[0-9a-f]{6}$/.test(c));
    gamePaletteCache = colors.length ? { name: p.name ?? "jogo", colors } : null;
  } catch {
    gamePaletteCache = null;
  }
  return gamePaletteCache;
}

/** Keys for palettes bigger than 32 colors ("." and space stay transparent). */
const GAME_KEYS = KEYS + "wxyzABCDEFGHIJKLMNOPQRSTUVWXYZ!$%&*+-/:;<=>?@^_|~()[]{}',`";

function builtinPalette(name) {
  const lower = String(name).toLowerCase();
  if (lower === "game" || lower === "jogo") {
    const gp = gamePalette();
    if (!gp) throw new Error(`"palette": "game" só funciona quando o projeto do jogo tem uma paleta (configurações do projeto no app).`);
    const map = { ".": null };
    gp.colors.forEach((c, i) => { if (i < GAME_KEYS.length) map[GAME_KEYS[i]] = "#" + c; });
    return map;
  }
  const colors = PALETTES[lower];
  if (!colors) throw new Error(`Paleta desconhecida: "${name}". Opções: ${Object.keys(PALETTES).join(", ")}${gamePalette() ? ", game" : ""}.`);
  const map = { ".": null };
  colors.forEach((c, i) => (map[KEYS[i]] = "#" + c));
  return map;
}

/**
 * Enforces the game palette: every visible pixel becomes the nearest palette color (weighted RGB distance) and
 * alpha becomes 0 or 255. Returns what was changed so the render can report it.
 */
function snapToGamePalette(anims) {
  const gp = gamePalette();
  if (!gp) return null;
  const pal = gp.colors.map((c) => [parseInt(c.slice(0, 2), 16), parseInt(c.slice(2, 4), 16), parseInt(c.slice(4, 6), 16)]);
  const exact = new Set(gp.colors.map((c) => parseInt(c, 16)));
  const cache = new Map();
  const nearest = (r, g, b) => {
    const key = (r << 16) | (g << 8) | b;
    let hit = cache.get(key);
    if (hit !== undefined) return hit;
    let best = 0, bestD = Infinity;
    for (let i = 0; i < pal.length; i++) {
      const [pr, pg, pb] = pal[i], rm = (r + pr) / 2, dr = r - pr, dg = g - pg, db = b - pb;
      const d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
      if (d < bestD) { bestD = d; best = i; }
    }
    cache.set(key, (hit = best));
    return hit;
  };
  const hex = (r, g, b) => "#" + [r, g, b].map((v) => v.toString(16).padStart(2, "0")).join("");
  let pixels = 0, alpha = 0;
  const swaps = new Map();
  const seen = new Set();
  for (const a of anims) for (const f of a.frames) {
    if (seen.has(f.img)) continue;
    seen.add(f.img);
    const d = f.img.data;
    for (let i = 0; i < d.length; i += 4) {
      if (d[i + 3] === 0) continue;
      if (d[i + 3] < 128) { d[i] = d[i + 1] = d[i + 2] = d[i + 3] = 0; alpha++; continue; }
      if (d[i + 3] !== 255) { d[i + 3] = 255; alpha++; }
      const key = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2];
      if (exact.has(key)) continue;
      const [nr, ng, nb] = pal[nearest(d[i], d[i + 1], d[i + 2])];
      const from = hex(d[i], d[i + 1], d[i + 2]);
      swaps.set(from, hex(nr, ng, nb));
      d[i] = nr; d[i + 1] = ng; d[i + 2] = nb;
      pixels++;
    }
  }
  return { name: gp.name, count: gp.colors.length, pixels, alpha, swaps };
}

function parseColor(c, where) {
  if (c === null || c === undefined || c === "transparent" || c === "") return [0, 0, 0, 0];
  if (Array.isArray(c)) return [c[0], c[1], c[2], c[3] ?? 255];
  let s = String(c).trim().replace(/^#/, "");
  if (s.length === 3 || s.length === 4) s = [...s].map((x) => x + x).join("");
  if (!/^[0-9a-f]{6}([0-9a-f]{2})?$/i.test(s)) throw new Error(`Cor inválida ${JSON.stringify(c)} (${where}). Use "#rrggbb" ou "#rrggbbaa".`);
  const n = (i) => parseInt(s.slice(i, i + 2), 16);
  return [n(0), n(2), n(4), s.length === 8 ? n(6) : 255];
}

function resolvePalette(p) {
  let map = {};
  if (typeof p === "string") map = builtinPalette(p);
  else if (p && typeof p === "object") {
    if (p.$base) map = builtinPalette(p.$base);
    for (const [k, v] of Object.entries(p)) if (k !== "$base") map[k] = v;
  }
  const out = new Map([[".", [0, 0, 0, 0]], [" ", [0, 0, 0, 0]]]);
  for (const [k, v] of Object.entries(map)) {
    if ([...k].length !== 1) throw new Error(`Chave de paleta "${k}" deve ter 1 caractere.`);
    out.set(k, parseColor(v, `paleta "${k}"`));
  }
  return out;
}

// ---------------------------------------------------------------- images
const blank = (w, h) => ({ w, h, data: new Uint8ClampedArray(w * h * 4) });
const clone = (img) => ({ w: img.w, h: img.h, data: new Uint8ClampedArray(img.data) });

function setPx(img, x, y, [r, g, b, a]) {
  if (x < 0 || y < 0 || x >= img.w || y >= img.h) return;
  const i = (y * img.w + x) * 4;
  img.data[i] = r; img.data[i + 1] = g; img.data[i + 2] = b; img.data[i + 3] = a;
}

/** Source-over compositing of src onto dst at (ox, oy). */
function over(dst, src, ox = 0, oy = 0) {
  for (let y = 0; y < src.h; y++) {
    for (let x = 0; x < src.w; x++) {
      const dx = x + ox, dy = y + oy;
      if (dx < 0 || dy < 0 || dx >= dst.w || dy >= dst.h) continue;
      const si = (y * src.w + x) * 4, di = (dy * dst.w + dx) * 4;
      const sa = src.data[si + 3] / 255;
      if (sa === 0) continue;
      const da = dst.data[di + 3] / 255;
      const oa = sa + da * (1 - sa);
      for (let c = 0; c < 3; c++)
        dst.data[di + c] = Math.round((src.data[si + c] * sa + dst.data[di + c] * da * (1 - sa)) / oa);
      dst.data[di + 3] = Math.round(oa * 255);
    }
  }
  return dst;
}

function shift(img, dx, dy) {
  const out = blank(img.w, img.h);
  return over(out, img, dx, dy);
}

function flip(img, horizontal) {
  const out = blank(img.w, img.h);
  for (let y = 0; y < img.h; y++)
    for (let x = 0; x < img.w; x++) {
      const sx = horizontal ? img.w - 1 - x : x, sy = horizontal ? y : img.h - 1 - y;
      const si = (sy * img.w + sx) * 4, di = (y * img.w + x) * 4;
      for (let c = 0; c < 4; c++) out.data[di + c] = img.data[si + c];
    }
  return out;
}

/** 1px outline (4-neighbourhood) around opaque pixels, drawn only on transparent pixels. */
function outline(img, color) {
  const out = clone(img);
  const opaque = (x, y) => x >= 0 && y >= 0 && x < img.w && y < img.h && img.data[(y * img.w + x) * 4 + 3] >= 128;
  for (let y = 0; y < img.h; y++)
    for (let x = 0; x < img.w; x++)
      if (!opaque(x, y) && (opaque(x - 1, y) || opaque(x + 1, y) || opaque(x, y - 1) || opaque(x, y + 1)))
        setPx(out, x, y, color);
  return out;
}

function scaleNearest(img, s) {
  if (s === 1) return img;
  const out = blank(img.w * s, img.h * s);
  for (let y = 0; y < out.h; y++)
    for (let x = 0; x < out.w; x++) {
      const si = (Math.floor(y / s) * img.w + Math.floor(x / s)) * 4, di = (y * out.w + x) * 4;
      for (let c = 0; c < 4; c++) out.data[di + c] = img.data[si + c];
    }
  return out;
}

function resizeNearest(img, w, h) {
  if (img.w === w && img.h === h) return img;
  const out = blank(w, h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const si = (Math.floor((y * img.h) / h) * img.w + Math.floor((x * img.w) / w)) * 4, di = (y * w + x) * 4;
      for (let c = 0; c < 4; c++) out.data[di + c] = img.data[si + c];
    }
  return out;
}

function crop(img, x0, y0, w, h) {
  const out = blank(w, h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const sx = x0 + x, sy = y0 + y;
      if (sx < 0 || sy < 0 || sx >= img.w || sy >= img.h) continue;
      const si = (sy * img.w + sx) * 4, di = (y * w + x) * 4;
      for (let c = 0; c < 4; c++) out.data[di + c] = img.data[si + c];
    }
  return out;
}

function readPng(file) {
  const png = PNG.sync.read(fss.readFileSync(file));
  return { w: png.width, h: png.height, data: new Uint8ClampedArray(png.data) };
}

async function writePng(file, img) {
  const png = new PNG({ width: img.w, height: img.h });
  png.data = Buffer.from(img.data.buffer, img.data.byteOffset, img.data.byteLength);
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, PNG.sync.write(png));
}

let resvg;
async function renderSvg(svg, w, h) {
  resvg ??= (await import("@resvg/resvg-js")).Resvg;
  const r = new resvg(svg, { fitTo: { mode: "width", value: w }, background: "rgba(0,0,0,0)" }).render();
  const img = { w: r.width, h: r.height, data: new Uint8ClampedArray(r.pixels) };
  if (img.w === w && img.h === h) return img;
  const out = blank(w, h); // keep the frame size: center (or crop) the rendered SVG
  return over(out, img, Math.round((w - img.w) / 2), Math.round((h - img.h) / 2));
}

// ---------------------------------------------------------------- source → frames
function paint(img, rows, palette, where, ox = 0, oy = 0, keepDot = false) {
  if (!Array.isArray(rows)) throw new Error(`${where}: "pixels" deve ser uma lista de linhas (strings).`);
  if (oy + rows.length > img.h) throw new Error(`${where}: ${rows.length} linhas não cabem na altura ${img.h}.`);
  rows.forEach((row, y) => {
    const chars = [...String(row)];
    if (ox + chars.length > img.w)
      throw new Error(`${where}, linha ${y}: ${chars.length} pixels não cabem na largura ${img.w}: "${row}"`);
    chars.forEach((ch, x) => {
      if (keepDot && (ch === "." || ch === " ")) return;
      const color = palette.get(ch);
      if (!color) throw new Error(`${where}, linha ${y}, coluna ${x}: "${ch}" não está na paleta.`);
      setPx(img, ox + x, oy + y, color);
    });
  });
}

class SpriteBuild {
  constructor(src, srcFile) {
    this.src = src;
    this.dir = path.dirname(srcFile);
    const size = src.size ?? [16, 16];
    [this.w, this.h] = Array.isArray(size) ? size : [size, size];
    if (!(this.w > 0 && this.h > 0 && this.w <= 2048 && this.h <= 2048)) throw new Error(`"size" inválido: ${JSON.stringify(size)}.`);
    this.palette = resolvePalette(src.palette);
    this.animations = src.animations ?? (src.frames ? { default: { frames: src.frames } } : null);
    if (!this.animations || Object.keys(this.animations).length === 0)
      throw new Error('O sprite precisa de "animations": { "nome": { "frames": [ … ] } } (ou "frames": [ … ]).');
    this.cache = new Map();
    this.resolving = new Set();
  }

  color(c, where) {
    return typeof c === "string" && [...c].length === 1 && this.palette.has(c) ? this.palette.get(c) : parseColor(c, where);
  }

  file(rel) {
    // relative to the workspace (cwd) first, then to the .sprite.json folder
    for (const p of [path.resolve(rel), path.resolve(this.dir, rel)]) if (fss.existsSync(p)) return p;
    throw new Error(`Arquivo não encontrado: ${rel}`);
  }

  async frameAt(ref) {
    const [anim, idx] = String(ref).split(/[/:]/);
    const a = this.animations[anim];
    const spec = a?.frames?.[Number(idx ?? 0)];
    if (!spec) throw new Error(`Quadro "${ref}" não existe (use "animação/índice", ex.: "idle/0").`);
    const key = `${anim}/${Number(idx ?? 0)}`;
    if (this.cache.has(key)) return this.cache.get(key);
    if (this.resolving.has(key)) throw new Error(`Referência circular em "${key}".`);
    this.resolving.add(key);
    const img = await this.frame(spec, key);
    this.resolving.delete(key);
    this.cache.set(key, img);
    return img;
  }

  async frame(spec, where) {
    if (typeof spec === "string") return clone(await this.frameAt(spec)); // shorthand: "idle/0"
    if (Array.isArray(spec)) spec = { pixels: spec };
    let img = spec.base !== undefined ? clone(await this.frameAt(spec.base)) : blank(this.w, this.h);
    for (const [i, layer] of (spec.layers ?? []).entries()) over(img, await this.frame(layer, `${where} camada ${i}`));
    if (spec.pixels) {
      const layer = blank(this.w, this.h);
      paint(layer, spec.pixels, this.palette, where);
      over(img, layer);
    }
    if (spec.svg || spec.svgFile) {
      const svg = spec.svg ?? (await fs.readFile(this.file(spec.svgFile), "utf8"));
      over(img, await renderSvg(svg, this.w, this.h));
    }
    if (spec.image) {
      let src = readPng(this.file(spec.image));
      if (spec.crop) src = crop(src, ...spec.crop);
      over(img, resizeNearest(src, this.w, this.h));
    }
    for (const p of spec.patch ?? []) paint(img, p.pixels, this.palette, `${where} patch`, p.at?.[0] ?? 0, p.at?.[1] ?? 0, true);
    for (const [x, y] of spec.erase ?? []) setPx(img, x, y, [0, 0, 0, 0]);
    if (spec.shift) img = shift(img, spec.shift[0] ?? 0, spec.shift[1] ?? 0);
    if (spec.flipX) img = flip(img, true);
    if (spec.flipY) img = flip(img, false);
    if (spec.outline) img = outline(img, this.color(spec.outline, `${where} outline`));
    return img;
  }

  async build() {
    const anims = [];
    for (const [name, a] of Object.entries(this.animations)) {
      const frames = a.frames ?? [];
      if (frames.length === 0) throw new Error(`Animação "${name}" não tem quadros.`);
      const fps = a.fps ?? 8;
      const list = [];
      for (let i = 0; i < frames.length; i++) {
        let img = await this.frameAt(`${name}/${i}`);
        if (this.src.outline) img = outline(img, this.color(this.src.outline, "outline"));
        const spec = frames[i];
        const duration = (typeof spec === "object" && !Array.isArray(spec) && spec.duration) || Math.round(1000 / fps);
        list.push({ img, duration });
      }
      anims.push({ name, fps, loop: a.loop !== false, pingPong: !!a.pingPong, frames: list });
    }
    return anims;
  }
}

// ---------------------------------------------------------------- asset kind (set by the app)
/** sprite | tile | animation — from .assetscraft/kind.json; missing = no restriction. */
function assetKind() {
  try {
    return JSON.parse(fss.readFileSync(path.resolve(".assetscraft", "kind.json"), "utf8")).kind ?? null;
  } catch {
    return null;
  }
}

function checkKind(anims) {
  const kind = assetKind();
  const total = anims.reduce((n, a) => n + a.frames.length, 0);
  if (kind === "sprite" && total !== 1)
    throw new Error(
      `Esta conversa é de SPRITE ESTÁTICO: a fonte deve ter exatamente 1 quadro (a pose idle), mas tem ${total} ` +
      `(${anims.map((a) => `${a.name}: ${a.frames.length}`).join(", ")}). Deixe só "idle" com 1 quadro; ` +
      "animações são feitas na seção Animação do app, a partir deste sprite.");
  if (kind === "tile") for (const a of anims) a.static = true; // tilesets never get GIFs
  if (kind === "animation" && !anims.some((a) => a.name === "idle"))
    throw new Error('Esta conversa anima um sprite existente: mantenha a animação "idle" com o quadro base (idle/0).');
}

// ---------------------------------------------------------------- render
function nextVersion(outDir, name) {
  const re = new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}-v(\\d+)\\.png$`, "i");
  let max = 0;
  if (fss.existsSync(outDir)) for (const f of fss.readdirSync(outDir)) { const m = re.exec(f); if (m) max = Math.max(max, +m[1]); }
  return max + 1;
}

async function writeGif(file, frames, loop, scale) {
  const enc = GIFEncoder();
  const all = frames.map((f) => scaleNearest(f.img, scale));
  // one shared palette for the animation; transparent pixels → index 0
  const colors = new Map();
  for (const img of all)
    for (let i = 0; i < img.data.length; i += 4)
      if (img.data[i + 3] >= 128) colors.set((img.data[i] << 16) | (img.data[i + 1] << 8) | img.data[i + 2], 0);
  let palette;
  if (colors.size <= 255) palette = [[0, 0, 0], ...[...colors.keys()].map((c) => [c >> 16, (c >> 8) & 255, c & 255])];
  else {
    const joined = new Uint8ClampedArray(all.reduce((n, i) => n + i.data.length, 0));
    let o = 0;
    for (const img of all) { joined.set(img.data, o); o += img.data.length; }
    palette = [[0, 0, 0], ...quantize(joined, 255, { format: "rgb565" })];
  }
  const lookup = new Map(palette.map((c, i) => [(c[0] << 16) | (c[1] << 8) | c[2], i]));
  all.forEach((img, fi) => {
    const index = new Uint8Array(img.w * img.h);
    const rgba = img.data;
    const nearest = colors.size > 255 ? applyPalette(rgba, palette.slice(1), "rgb565") : null;
    for (let p = 0; p < index.length; p++) {
      if (rgba[p * 4 + 3] < 128) { index[p] = 0; continue; }
      index[p] = nearest ? nearest[p] + 1 : lookup.get((rgba[p * 4] << 16) | (rgba[p * 4 + 1] << 8) | rgba[p * 4 + 2]);
    }
    enc.writeFrame(index, img.w, img.h, {
      palette, transparent: true, transparentIndex: 0, delay: frames[fi].duration, dispose: 2,
      repeat: fi === 0 ? (loop ? 0 : -1) : undefined,
    });
  });
  enc.finish();
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, enc.bytes());
}

/** Upscaled copy on a checkerboard with a grid between frames — for looking at the result. */
function previewImage(img, zoom, cellW, cellH) {
  const out = blank(img.w * zoom, img.h * zoom);
  const checker = Math.max(4, zoom * 2);
  for (let y = 0; y < out.h; y++)
    for (let x = 0; x < out.w; x++) {
      const dark = (Math.floor(x / checker) + Math.floor(y / checker)) % 2 === 0;
      const v = dark ? 58 : 74;
      const si = (Math.floor(y / zoom) * img.w + Math.floor(x / zoom)) * 4, di = (y * out.w + x) * 4;
      const a = img.data[si + 3] / 255;
      for (let c = 0; c < 3; c++) out.data[di + c] = Math.round(img.data[si + c] * a + v * (1 - a));
      out.data[di + 3] = 255;
      const gx = cellW && x % (cellW * zoom) === 0, gy = cellH && y % (cellH * zoom) === 0;
      if ((gx || gy) && x > 0 && y > 0) { out.data[di] = 255; out.data[di + 1] = 0; out.data[di + 2] = 170; }
    }
  return out;
}

function autoZoom(w, h, target = 768) {
  return Math.max(1, Math.min(16, Math.floor(target / Math.max(w, h))));
}

async function cmdRender(srcFile, opts) {
  const src = JSON.parse(await fs.readFile(srcFile, "utf8"));
  const name = (src.name ?? path.basename(srcFile).replace(/\.sprite\.json$|\.json$/i, ""))
    .toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "sprite";
  const b = new SpriteBuild(src, srcFile);
  const anims = await b.build();
  checkKind(anims);
  const snapped = snapToGamePalette(anims);

  const ex = src.export ?? {};
  const scale = Math.max(1, Math.round(Number(opts.scale ?? ex.scale ?? 1)));
  const pad = Math.max(0, Math.round(Number(ex.padding ?? 0)));
  const fw = b.w * scale, fh = b.h * scale;
  const total = anims.reduce((n, a) => n + a.frames.length, 0);

  // layout: one row per animation (engine friendly); "columns" = flow layout instead
  const cells = [];
  if (ex.columns) {
    let i = 0;
    for (const a of anims) for (const f of a.frames) { cells.push({ a, f, col: i % ex.columns, row: Math.floor(i / ex.columns) }); i++; }
  } else {
    anims.forEach((a, row) => a.frames.forEach((f, col) => cells.push({ a, f, col, row })));
  }
  const cols = Math.max(...cells.map((c) => c.col)) + 1, rows = Math.max(...cells.map((c) => c.row)) + 1;
  const sheet = blank(cols * (fw + pad) + pad, rows * (fh + pad) + pad);
  const frames = [];
  const tags = [];
  for (const a of anims) {
    const from = frames.length;
    for (const [i, f] of a.frames.entries()) {
      const c = cells.find((x) => x.f === f);
      const x = pad + c.col * (fw + pad), y = pad + c.row * (fh + pad);
      over(sheet, scaleNearest(f.img, scale), x, y);
      frames.push({
        filename: `${a.name} ${i}`, frame: { x, y, w: fw, h: fh }, rotated: false, trimmed: false,
        spriteSourceSize: { x: 0, y: 0, w: fw, h: fh }, sourceSize: { w: fw, h: fh }, duration: f.duration,
      });
    }
    const tag = { name: a.name, from, to: frames.length - 1, direction: a.pingPong ? "pingpong" : "forward" };
    if (!a.loop) tag.repeat = "1";
    tags.push(tag);
  }

  const outDir = path.resolve("out");
  const v = nextVersion(outDir, name);
  const stem = `${name}-v${v}`;
  const meta = {
    frames,
    meta: {
      app: "AssetsCraft", version: "1", image: `${stem}.png`, format: "RGBA8888",
      size: { w: sheet.w, h: sheet.h }, scale: String(scale), frameTags: tags,
      assetscraft: {
        source: path.relative(process.cwd(), path.resolve(srcFile)).replace(/\\/g, "/"),
        frameSize: { w: b.w, h: b.h }, scale, padding: pad,
        animations: Object.fromEntries(anims.map((a) => [a.name, { fps: a.fps, loop: a.loop, pingPong: a.pingPong, frames: a.frames.length }])),
      },
    },
  };
  await writePng(path.join(outDir, `${stem}.png`), sheet);
  await fs.writeFile(path.join(outDir, `${stem}.json`), JSON.stringify(meta, null, 2));
  await fs.mkdir(path.join(outDir, ".sources"), { recursive: true });
  await fs.copyFile(srcFile, path.join(outDir, ".sources", `${stem}.sprite.json`));

  const gifScale = Math.max(1, Math.round(Number(ex.gifScale ?? Math.max(1, Math.floor(128 / Math.max(fw, fh))))));
  const gifs = [];
  for (const a of anims) {
    if (a.frames.length < 2 || a.static) continue;
    const seq = a.pingPong ? [...a.frames, ...a.frames.slice(1, -1).reverse()] : a.frames;
    const file = path.join(outDir, "gif", `${stem}-${a.name.replace(/[^a-z0-9_-]+/gi, "-")}.gif`);
    await writeGif(file, seq.map((f) => ({ ...f, img: scaleNearest(f.img, scale) })), a.loop, gifScale);
    gifs.push(path.relative(process.cwd(), file).replace(/\\/g, "/"));
  }

  const zoom = autoZoom(sheet.w, sheet.h);
  const previewFile = path.join(outDir, ".preview", `${stem}.png`);
  await writePng(previewFile, previewImage(sheet, zoom, pad ? 0 : fw, pad ? 0 : fh));

  const rel = (p) => path.relative(process.cwd(), p).replace(/\\/g, "/");
  console.log(`ok  out/${stem}.png  (${sheet.w}x${sheet.h}, quadro ${fw}x${fh}, ${total} quadros, escala ${scale}x)`);
  console.log(`    out/${stem}.json  (metadados: frames + frameTags, formato Aseprite JSON array)`);
  for (const a of anims) console.log(`    animação "${a.name}": ${a.frames.length} quadros, ${a.fps} fps${a.loop ? ", loop" : ""}${a.pingPong ? ", ping-pong" : ""}`);
  for (const g of gifs) console.log(`    ${g}`);
  console.log(`    conferir: leia ${rel(previewFile)} (ampliado ${zoom}x, linhas magenta = limites dos quadros)`);
  if (snapped) {
    if (snapped.pixels === 0 && snapped.alpha === 0) console.log(`    paleta do jogo "${snapped.name}" (${snapped.count} cores): ok, todas as cores são da paleta`);
    else {
      const list = [...snapped.swaps].slice(0, 12).map(([a, b]) => `${a}→${b}`).join(", ");
      console.log(`    AVISO paleta do jogo "${snapped.name}": ${snapped.pixels} pixels fora da paleta foram trocados pela cor mais próxima` +
        (snapped.swaps.size ? ` (${list}${snapped.swaps.size > 12 ? ", …" : ""})` : "") +
        (snapped.alpha ? `; ${snapped.alpha} pixels semitransparentes viraram opacos/transparentes` : "") +
        `. Confira o preview; se ficou ruim, redesenhe usando "palette": "game".`);
    }
  }
}

// ---------------------------------------------------------------- preview / inspect / palettes
async function cmdPreview(file, opts) {
  const img = readPng(file);
  const zoom = Math.max(1, Math.round(Number(opts.zoom ?? autoZoom(img.w, img.h))));
  const [cw, ch] = opts.cell ? String(opts.cell).split("x").map(Number) : [0, 0];
  const out = path.resolve("out", ".preview", path.basename(file, path.extname(file)) + ".png");
  await writePng(out, previewImage(img, zoom, cw, ch));
  console.log(`ok  ${path.relative(process.cwd(), out).replace(/\\/g, "/")} (${img.w}x${img.h} ampliado ${zoom}x)`);
}

async function cmdInspect(file, opts) {
  const img = readPng(file);
  const maxColors = Number(opts["max-colors"] ?? 48);
  const charset = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#@$%&*+=?!<>^~";
  const counts = new Map();
  for (let i = 0; i < img.data.length; i += 4) {
    if (img.data[i + 3] < 128) continue;
    const hex = "#" + [0, 1, 2].map((c) => img.data[i + c].toString(16).padStart(2, "0")).join("");
    counts.set(hex, (counts.get(hex) ?? 0) + 1);
  }
  const sorted = [...counts.entries()].sort((a, b) => b[1] - a[1]);
  console.log(`${path.basename(file)}: ${img.w}x${img.h}, ${sorted.length} cores opacas`);
  if (sorted.length > maxColors) {
    console.log(`Muitas cores (> ${maxColors}): provavelmente não é pixel art (foto/antialias). Mais usadas:`);
    console.log(sorted.slice(0, 16).map(([c, n]) => `${c} ×${n}`).join("  "));
    console.log("Para usar a imagem num sprite, prefira { \"image\": \"…\", \"crop\": [x,y,w,h] } (reduz por vizinho mais próximo).");
    return;
  }
  const key = new Map(sorted.map(([c], i) => [c, charset[i]]));
  const palette = Object.fromEntries([[".", null], ...sorted.map(([c]) => [key.get(c), c])]);
  const rowsOf = (x0, y0, w, h) => {
    const rows = [];
    for (let y = y0; y < y0 + h; y++) {
      let s = "";
      for (let x = x0; x < x0 + w; x++) {
        const i = (y * img.w + x) * 4;
        if (x >= img.w || y >= img.h || img.data[i + 3] < 128) { s += "."; continue; }
        s += key.get("#" + [0, 1, 2].map((c) => img.data[i + c].toString(16).padStart(2, "0")).join(""));
      }
      rows.push(s);
    }
    return rows;
  };
  const [cw, ch] = opts.cell ? String(opts.cell).split("x").map(Number) : [img.w, img.h];
  const animations = {};
  for (let r = 0; r * ch < img.h; r++) {
    const frames = [];
    for (let c = 0; c * cw < img.w; c++) {
      const rows = rowsOf(c * cw, r * ch, cw, ch);
      if (rows.some((s) => /[^.]/.test(s))) frames.push({ pixels: rows });
    }
    if (frames.length) animations[opts.cell ? `linha${r}` : "default"] = { fps: 8, frames };
  }
  const draft = { name: path.basename(file, path.extname(file)), size: [cw, ch], palette, animations };
  if (opts.to) {
    await fs.mkdir(path.dirname(path.resolve(opts.to)), { recursive: true });
    await fs.writeFile(opts.to, JSON.stringify(draft, null, 2));
    console.log(`ok  ${opts.to} (${Object.values(animations).reduce((n, a) => n + a.frames.length, 0)} quadros ${cw}x${ch}; renomeie as animações)`);
    return;
  }
  if (img.w * img.h > 64 * 64) {
    console.log("Imagem grande: use --to sprites/<nome>.sprite.json para gravar a grade num arquivo e leia o arquivo.");
    console.log("paleta:", JSON.stringify(palette));
    return;
  }
  console.log(JSON.stringify(draft, null, 1));
}

function cmdPalettes() {
  const gp = gamePalette();
  if (gp) {
    console.log(`game — PALETA DO JOGO "${gp.name}" (${gp.colors.length} cores, obrigatória) — use "palette": "game"`);
    console.log("  " + gp.colors.map((c, i) => `${i < GAME_KEYS.length ? GAME_KEYS[i] : "-"}=#${c}`).join(" "));
    console.log("(as paletas abaixo não podem ser usadas neste projeto)");
  }
  for (const name of Object.keys(PALETTES)) {
    const map = builtinPalette(name);
    console.log(`${name} (${PALETTES[name].length} cores) — use "palette": "${name}" ou { "$base": "${name}", "x": "#hex" }`);
    console.log("  " + Object.entries(map).filter(([k]) => k !== ".").map(([k, v]) => `${k}=${v}`).join(" "));
  }
}

// ---------------------------------------------------------------- cli
function parseArgs(argv) {
  const pos = [], opts = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith("--")) {
      const k = argv[i].slice(2);
      if (i + 1 < argv.length && !argv[i + 1].startsWith("--")) opts[k] = argv[++i];
      else opts[k] = true;
    } else pos.push(argv[i]);
  }
  return { pos, opts };
}

const USAGE = `uso:
  node tools/sprite.mjs render sprites/<nome>.sprite.json [--scale N]
  node tools/sprite.mjs preview <arquivo.png> [--zoom N] [--cell WxH]
  node tools/sprite.mjs inspect <arquivo.png> [--cell WxH] [--to sprites/x.sprite.json]
  node tools/sprite.mjs palettes`;

const [cmd, ...rest] = process.argv.slice(2);
const { pos, opts } = parseArgs(rest);
try {
  if (cmd === "render" && pos[0]) await cmdRender(pos[0], opts);
  else if (cmd === "preview" && pos[0]) await cmdPreview(pos[0], opts);
  else if (cmd === "inspect" && pos[0]) await cmdInspect(pos[0], opts);
  else if (cmd === "palettes") cmdPalettes();
  else { console.log(USAGE); process.exitCode = cmd ? 1 : 0; }
} catch (e) {
  console.error("erro: " + (e?.message ?? e));
  process.exitCode = 1;
}
