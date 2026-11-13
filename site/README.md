# Vigil website

This is the landing page for Vigil. It is static HTML and CSS with a small script and no framework.

## Settings

All the settings are in `config.json`:

| Key | Used for |
|---|---|
| `siteUrl` | canonical link, Open Graph and Twitter images, `sitemap.xml`, `robots.txt` |
| `owner`, `repo` | the GitHub link and the releases link |
| `installer` | the download link: `https://github.com/<owner>/<repo>/releases/latest/download/<installer>` |
| `version` | the version shown under the last Download button |
| `languages` | the languages, in the order the menu lists them; the first is the default, at `/` |

`node build.mjs` writes one page per language to `dist/` (`/`, `/pt/`, `/es/`, `/fr/`) and fills in each `{{PLACEHOLDER}}` from these settings. It needs only Node 18 or later and installs no packages.

## Languages

The page is written once, in `src/index.html`, with `{{t.section.key}}` where the text goes. Each language has its
file in `i18n/` (English, Portuguese from Portugal, Spanish and French). If a file is missing a key the page uses,
the build stops and names it.

To add a language, copy `i18n/en.json` to `i18n/<id>.json`, translate it, and add `<id>` to `languages` in
`config.json`. The language menu, the `hreflang` links and `sitemap.xml` pick it up on their own.

The names of the designs and the labels drawn on the screen mockups (CPU, MEMORY, …) stay in English, as in the
app. The share picture (`og.png`) is the same for every language.

## Run it locally

```sh
cd site
node build.mjs
npx serve dist        # or any static server, then open the address it prints
```

## Deploy on Vercel

1. Import the repository in Vercel.
2. Set **Root Directory** to `site`.
3. Leave everything else as it is: `vercel.json` already sets the build command (`node build.mjs`) and the output directory (`dist`).

After the first deploy, put the real address in `siteUrl` if it is different, then deploy again.

## Files

```
src/index.html     the page, with {{t.…}} where the translated text goes
i18n/*.json        the text of the page in each language
src/styles.css     styles, with the colours and fonts as tokens at the top (--accent, --glow, …)
src/main.js        the motion (see below), the clocks and closing the language menu
src/favicon.*      the app icon, small version (docs/brand/vigil-icon-small.svg)
src/og.png         the 1200×630 picture shown when a link is shared
tools/og.html      the source of og.png
```

To redraw `og.png` after changing `tools/og.html`:

```sh
chrome --headless=new --hide-scrollbars --force-device-scale-factor=1 --virtual-time-budget=5000 \
  --window-size=1200,630 --screenshot=src/og.png tools/og.html
```

## Motion

All of it is in `src/main.js` and at the end of `src/styles.css`, with no libraries:

- The hero screen switches on: the backlight comes up with a short flicker and a band of light runs down it. The
  device leans a little towards the pointer.
- One set of made-up sensors drives every screen on the page. The readings wander like a PC at rest, every 2
  seconds, and the graphs scroll along with them.
- The clocks show the visitor's time, and the world clocks show the real time in each city.
- Sections fade in and rise 16 px the first time they are seen. Graphs, gauges and bars draw themselves in, and the
  sensor values count up.
- The words of the statement light up one by one as it scrolls past.
- The colon on the hero clock fades in and out.

Only transform, opacity and colour change, and the readings pause while the tab is hidden. With *reduce motion*
turned on in the system settings, none of this runs and only the clocks change. Without JavaScript, the page shows
everything and nothing moves.
