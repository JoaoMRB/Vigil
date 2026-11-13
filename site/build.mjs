// Builds the site into dist/: one page per language from src/index.html and i18n/*.json, with the
// {{PLACEHOLDERS}} filled from config.json. No dependencies: `node build.mjs`.
import { cpSync, readFileSync, writeFileSync, rmSync, mkdirSync, readdirSync, statSync } from 'node:fs';
import { join, extname, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const read = path => readFileSync(join(root, path), 'utf8');
const config = JSON.parse(read('config.json'));
const repoUrl = `https://github.com/${config.owner}/${config.repo}`;
const siteUrl = config.siteUrl.replace(/\/$/, '');

const values = {
  SITE_URL: siteUrl,
  REPO_URL: repoUrl,
  DOWNLOAD_URL: `${repoUrl}/releases/latest/download/${config.installer}`,
  RELEASES_URL: `${repoUrl}/releases`,
  VERSION: config.version,
  YEAR: String(new Date().getFullYear()),
  BUILD_DATE: new Date().toISOString().slice(0, 10)
};

// Languages, in the order the menu lists them. The first is the default and lives at the root; the others at /<id>/.
const languages = config.languages.map((id, index) => {
  const strings = JSON.parse(read(`i18n/${id}.json`));
  const path = index === 0 ? '/' : `/${id}/`;
  return { id, path, url: siteUrl + path, strings };
});

function fill(text, extra, file) {
  return text.replace(/\{\{([A-Za-z_.]+)\}\}/g, (match, key) => {
    if (key in extra) return extra[key];
    if (key in values) return values[key];
    throw new Error(`${file}: unknown placeholder ${match}`);
  });
}

// {{t.section.key}} comes from the language's file. Every language must have every key the page uses, so a missing
// translation stops the build instead of showing up as a hole in the page.
function translate(html, language) {
  return html.replace(/\{\{t\.([A-Za-z_.]+)\}\}/g, (match, key) => {
    const value = key.split('.').reduce((node, part) => node?.[part], language.strings);
    if (typeof value !== 'string') throw new Error(`i18n/${language.id}.json: missing "${key}"`);
    return value;
  });
}

const src = join(root, 'src');
const out = join(root, 'dist');
rmSync(out, { recursive: true, force: true });
cpSync(src, out, { recursive: true });

// The stylesheet is small: putting it in the page saves a round trip before the first paint.
// Comments and runs of whitespace go; nothing else is rewritten.
const css = readFileSync(join(src, 'styles.css'), 'utf8')
  .replace(/\/\*[\s\S]*?\*\//g, '')
  .replace(/\s+/g, ' ')
  .replace(/\s*([{};,>])\s*/g, '$1')
  .trim();
const link = '<link rel="stylesheet" href="/styles.css">';
let template = readFileSync(join(src, 'index.html'), 'utf8');
if (!template.includes(link)) throw new Error('index.html: stylesheet link not found');
template = template.replace(link, `<style>${css}</style>`);
rmSync(join(out, 'styles.css'));

const alternates = [
  ...languages.map(l => `<link rel="alternate" hreflang="${l.strings.lang.code}" href="${l.url}">`),
  `<link rel="alternate" hreflang="x-default" href="${languages[0].url}">`
].join('\n');

for (const language of languages) {
  const { lang } = language.strings;
  const current = other => (other === language ? ' aria-current="page"' : '');
  const extra = {
    LANG: lang.code,
    LANG_SHORT: lang.short,
    LANG_NAME: lang.name,
    OG_LOCALE: lang.ogLocale,
    PAGE_URL: language.url,
    ALTERNATES: alternates,
    LANG_MENU: languages.map(l =>
      `          <li><a href="${l.path}" hreflang="${l.strings.lang.code}" lang="${l.strings.lang.code}"${current(l)}>${l.strings.lang.name}</a></li>`).join('\n'),
    LANG_LINKS: languages.map(l =>
      `      <a href="${l.path}" hreflang="${l.strings.lang.code}" lang="${l.strings.lang.code}"${current(l)}>${l.strings.lang.name}</a>`).join('\n')
  };
  // Translate first: some strings carry placeholders of their own, such as {{VERSION}}.
  const page = fill(translate(template, language), extra, `index.html (${language.id})`);
  const dir = join(out, language.path);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'index.html'), page);
}

// Every page in the sitemap names its translations, so search engines show each person their own language.
const sitemapLinks = languages.map(l => `    <xhtml:link rel="alternate" hreflang="${l.strings.lang.code}" href="${l.url}"/>`).join('\n');
writeFileSync(join(out, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${languages.map(l => `  <url>
    <loc>${l.url}</loc>
    <lastmod>${values.BUILD_DATE}</lastmod>
${sitemapLinks}
  </url>`).join('\n')}
</urlset>
`);

const templated = new Set(['.xml', '.txt', '.webmanifest']);
const files = dir => readdirSync(dir).flatMap(name => {
  const path = join(dir, name);
  return statSync(path).isDirectory() ? files(path) : [path];
});
for (const file of files(out).filter(f => templated.has(extname(f)))) {
  writeFileSync(file, fill(readFileSync(file, 'utf8'), {}, file));
}

console.log(`Built dist/ for ${siteUrl} (v${values.VERSION}): ${languages.map(l => l.path).join(' ')}`);
