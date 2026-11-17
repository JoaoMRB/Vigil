// Checks the settings window's texts (Vigil/Ui/i18n/*.json) against the code that uses them:
//   - every language has exactly the keys of en.json, with the same {placeholders};
//   - every key the page (index.html, app.js) and the app (UiText.Get in *.cs) ask for is in en.json,
//     including the ones built from ids: view.<id>.*, design.<id>.*, metric.<id>, cpu.<state>.*, status.<key>.
// Run: node tools/check-i18n.mjs   (exit code 1 when something is wrong)

import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'Vigil');
const i18n = join(root, 'Ui', 'i18n');
const read = path => readFileSync(path, 'utf8');
const problems = [];

const en = JSON.parse(read(join(i18n, 'en.json')));
const placeholders = text => [...text.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort().join(',');

// 1. Every language against English.
for (const file of readdirSync(i18n).filter(f => f.endsWith('.json') && f !== 'en.json')) {
  const texts = JSON.parse(read(join(i18n, file)));
  for (const key of Object.keys(en)) {
    if (!(key in texts)) problems.push(`${file}: missing ${key}`);
    else if (placeholders(texts[key]) !== placeholders(en[key])) problems.push(`${file}: ${key} has {${placeholders(texts[key])}}, English has {${placeholders(en[key])}}`);
    else if (!texts[key].trim()) problems.push(`${file}: ${key} is empty`);
  }
  for (const key of Object.keys(texts)) if (!(key in en)) problems.push(`${file}: ${key} is not in en.json`);
}

// 2. The keys the code asks for.
const need = new Set();
const html = read(join(root, 'Ui', 'index.html'));
for (const m of html.matchAll(/data-i18n="([^"]+)"/g)) need.add(m[1]);
for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) for (const pair of m[1].split(';')) need.add(pair.split('=')[1]);

const js = read(join(root, 'Ui', 'app.js'));
for (const m of js.matchAll(/\bt(?:Nodes)?\('([\w.-]+)'/g)) need.add(m[1]);
// t(cond ? 'a' : 'b'): every quoted key inside a t( ... ) call
for (const m of js.matchAll(/\bt\(([^()]*\?[^()]*)\)/g)) for (const k of m[1].matchAll(/'([a-z]+\.[\w.-]+)'/g)) need.add(k[1]);

const cs = readdirSync(root, { recursive: true }).filter(f => f.endsWith('.cs') && !f.includes('obj') && !f.includes('bin'));
for (const file of cs) for (const m of read(join(root, file)).matchAll(/UiText\.Get\("([\w.-]+)"/g)) need.add(m[1]);

// Keys built from ids.
const ids = (file, pattern) => [...read(join(root, file)).matchAll(pattern)].map(m => m[1]);
const views = (/Views = \[([^\]]+)\]/.exec(read(join(root, 'MainForm.cs')))[1].match(/"(\w+)"/g) || []).map(s => s.slice(1, -1));
for (const v of views) for (const part of ['name', 'short', 'desc']) need.add(`view.${v}.${part}`);
for (const d of ids('Render/SystemDesigns.cs', /new\("(\w+)", "[^"]*", "[^"]*"\)/g)) for (const part of ['name', 'desc']) need.add(`design.${d}.${part}`);
for (const metric of ids('Render/SystemDesigns.cs', /new\("([\w-]+)", "[^"]*"\),?\s*$/gm)) need.add(`metric.${metric}`);
for (const state of ['off', 'denied', 'driver', 'unsupported']) for (const part of ['title', 'desc']) need.add(`cpu.${state}.${part}`);
for (const key of ids('MainForm.cs', /\("\w+", "(\w+)"\)/g)) need.add(`status.${key}`);
for (const value of ['dim', 'off', 'keep']) need.add(`system.lock.${value}`);
for (const value of ['off', 'system', 'clock']) need.add(`views.game.${value}`);

for (const key of need) if (!(key in en)) problems.push(`en.json: missing ${key}, which the code uses`);
for (const key of Object.keys(en)) {
  const used = need.has(key) || js.includes(`'${key}'`);
  if (!used) problems.push(`en.json: ${key} is not used anywhere`);
}

if (problems.length) {
  console.error(problems.join('\n'));
  console.error(`\n${problems.length} problem(s).`);
  process.exit(1);
}
console.log(`OK: ${Object.keys(en).length} texts, ${readdirSync(i18n).filter(f => f.endsWith('.json')).length} languages, ${need.size} keys used.`);
