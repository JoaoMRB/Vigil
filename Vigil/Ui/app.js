'use strict';

/*
 * The settings window. The app (MainForm.cs) owns the settings: this page shows them, sends every change as a
 * message, and redraws from the settings the app sends back. Nothing is stored here.
 *
 * Every text comes from the app's i18n\<language>.json, sent with init as strings, and is looked up with t(key).
 *
 * App -> page: init {lang, strings, ...}, settings, status {state, key, detail}, frame, cityResults,
 *              system {autostart, admin, elevated, allUsers},
 *              update {version, current, notes, hasPage, installing, failure}
 * Page -> app: ready, set {key, value}, searchCity {id, name}, autostart {value}, admin {value}, diagnostics,
 *              hold {view}, screen {on}, update {action: install | page}
 */

const host = window.chrome && window.chrome.webview;
const send = message => host && host.postMessage(message);
const $ = id => document.getElementById(id);

let model = null;     // the init message: themes, fonts, views, cities, limits, the language and its texts
let settings = null;  // the latest settings from the app
let strings = {};     // the window's texts in the user's language, English where a translation lacks one

/** A text by its key, with "{name}" filled in from vars. A missing key shows as itself, which is easy to spot. */
function t(key, vars) {
  const text = strings[key];
  if (text === undefined) return key;
  return vars ? text.replace(/\{(\w+)\}/g, (all, name) => (name in vars ? String(vars[name]) : all)) : text;
}

/** Like t, for a text with a node in it: "{city}" becomes the element given for city. */
function tNodes(key, nodes) {
  return (strings[key] ?? key).split(/(\{\w+\})/).filter(Boolean)
    .map(part => { const name = /^\{(\w+)\}$/.exec(part); return name && nodes[name[1]] ? nodes[name[1]] : part; });
}

/** The page's own texts, once: data-i18n for an element's text, data-i18n-attr="attribute=key;..." for attributes. */
function translatePage() {
  document.documentElement.lang = model.lang || 'en';
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.dataset.i18n);
  for (const node of document.querySelectorAll('[data-i18n-attr]')) {
    for (const pair of node.dataset.i18nAttr.split(';')) {
      const [attribute, key] = pair.split('=');
      node.setAttribute(attribute, t(key));
    }
  }
  delete document.body.dataset.loading;
}

host && host.addEventListener('message', event => receive(event.data));
document.addEventListener('DOMContentLoaded', () => send({ type: 'ready' }));

function receive(message) {
  switch (message.type) {
    case 'init':
      model = message;
      settings = message.settings;
      strings = message.strings || {};
      translatePage();
      build();
      sync();
      syncSystem(message.system);
      break;
    case 'settings':
      settings = message.settings;
      sync();
      break;
    case 'status':
      showStatus(message.state, t(`status.${message.key}`, { detail: message.detail }));
      break;
    case 'frame':
      $('preview').src = message.png;
      showControls(message);
      break;
    case 'cityResults':
      showCityResults(message);
      break;
    case 'system':
      syncSystem(message);
      break;
    case 'diagnostics':
      showCopied(message.copied);
      break;
    case 'cpuTemp':
      showCpuHint(message);
      break;
    case 'update':
      showUpdate(message);
      break;
    case 'designThumbs':
      for (const thumb of message.thumbs) {
        const img = document.querySelector('img[data-design="' + thumb.id + '"]');
        if (img) img.src = thumb.image;
      }
      break;
  }
}

/* ------------------------------ update banner ------------------------------ */

let updateLater = '';    // the version put off with Later: hidden until the window opens again
let updateBound = false;

/** The banner for a newer version: {version, current, notes, hasPage, installing, failure}. No version, no banner. */
function showUpdate(info) {
  const banner = $('update');
  if (!updateBound) {
    updateBound = true;
    $('update-install').addEventListener('click', () => send({ type: 'update', action: 'install' }));
    $('update-page').addEventListener('click', () => send({ type: 'update', action: 'page' }));
    $('update-later').addEventListener('click', () => {
      updateLater = banner.dataset.version || '';
      banner.hidden = true;
    });
  }
  banner.hidden = !info.version || (info.version === updateLater && !info.installing);
  if (banner.hidden) return;
  banner.dataset.version = info.version;

  $('update-title').textContent = t('update.available', { version: info.version });
  const desc = $('update-desc');
  desc.dataset.state = info.failure ? 'failed' : '';
  desc.textContent = info.installing
    ? t('update.downloading')
    : info.failure
      ? t('update.failed', { error: info.failure })
      : t('update.current', { version: info.current });
  const install = $('update-install');
  install.disabled = info.installing;
  install.textContent = t(info.installing ? 'update.installing' : info.failure ? 'update.retry' : 'update.install');
  $('update-later').hidden = info.installing;

  const notes = $('update-notes');
  notes.hidden = !info.notes;
  if (notes.dataset.version !== info.version) {
    notes.dataset.version = info.version;
    $('update-notes-body').replaceChildren(...markdown(info.notes || ''));
  }
  $('update-page').hidden = !info.hasPage;
}

/**
 * The little of Markdown release notes use: headings, lists, paragraphs, **bold**, `code` and [links](...), the
 * links as their text only. Everything becomes text nodes and a few plain elements: nothing from the notes is
 * ever read as HTML.
 */
function markdown(text) {
  const blocks = [];
  let list = null;
  let paragraph = [];
  const endParagraph = () => {
    if (paragraph.length) blocks.push(el('p', {}, inline(paragraph.join(' '))));
    paragraph = [];
  };
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    const heading = /^#{1,6}\s+(.*)$/.exec(line);
    const item = /^[-*+]\s+(.*)$/.exec(line);
    if (item) {
      endParagraph();
      if (!list) blocks.push(list = el('ul'));
      list.append(el('li', {}, inline(item[1])));
      continue;
    }
    if (!line || heading) {
      endParagraph();
      list = null;
      if (heading) blocks.push(el('p', { class: 'notes-heading' }, inline(heading[1])));
      continue;
    }
    if (list && /^\s{2,}\S/.test(raw)) {
      // A list item that goes on over the next line.
      list.lastChild.append(' ', ...inline(line));
      continue;
    }
    list = null;
    paragraph.push(line);
  }
  endParagraph();
  return blocks;
}

function inline(text) {
  const nodes = [];
  const pattern = /\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\([^)]*\)/g;
  // Single *emphasis* or _emphasis_: the words without the marks.
  const plain = s => document.createTextNode(s.replace(/(^|[\s(])[*_](\S(?:.*?\S)?)[*_](?=[\s).,:;!?]|$)/g, '$1$2'));
  let last = 0;
  let match;
  while ((match = pattern.exec(text))) {
    if (match.index > last) nodes.push(plain(text.slice(last, match.index)));
    if (match[1] !== undefined) nodes.push(el('strong', {}, [plain(match[1])]));
    else if (match[2] !== undefined) nodes.push(el('code', { text: match[2] }));
    else nodes.push(plain(match[3]));
    last = pattern.lastIndex;
  }
  if (last < text.length) nodes.push(plain(text.slice(last)));
  return nodes;
}

/* ------------------------------ CPU temperature guide ------------------------------ */

/** The guide's states, each with a title and a text under cpu.<state>. in the texts. */
const cpuHints = new Set(['off', 'denied', 'driver', 'unsupported']);

let lastCpuHint = '';
function showCpuHint(info) {
  const show = cpuHints.has(info.state) && info.wanted && !info.dismissed;
  $('cpu-hint').hidden = !show;
  if (!show) return;
  // Redraw only when something changed, so a button never moves under the pointer.
  const key = JSON.stringify(info);
  if (key === lastCpuHint) return;
  lastCpuHint = key;

  $('cpu-hint-title').textContent = t(`cpu.${info.state}.title`);
  $('cpu-hint-desc').textContent = t(`cpu.${info.state}.desc`);
  const steps = $('cpu-hint-steps');
  steps.replaceChildren();
  if (info.state === 'off' || info.state === 'driver') {
    steps.append(
      el('li', { 'data-done': String(info.admin), text: t('cpu.stepMode') }),
      el('li', { 'data-done': String(info.pawnio), text: t('cpu.stepDriver') }));
  }

  const action = (label, name) => {
    const button = el('button', { class: 'button', type: 'button', text: label });
    button.addEventListener('click', () => send({ type: 'cpuTemp', action: name }));
    return button;
  };
  const actions = $('cpu-hint-actions');
  actions.replaceChildren();
  if (info.state === 'off' && !info.admin) actions.append(action(t('cpu.turnOn'), 'admin'));
  if (info.state === 'denied') actions.append(action(t('cpu.retry'), 'admin'));
  if (!info.pawnio && info.state !== 'denied') actions.append(action(t('cpu.getPawnio'), 'pawnio'));
  if (info.state === 'driver' || info.state === 'unsupported') actions.append(action(t('cpu.check'), 'recheck'));
  const later = el('button', { class: 'link', type: 'button', text: t('cpu.notNow') });
  later.addEventListener('click', () => send({ type: 'cpuTemp', action: 'dismiss' }));
  actions.append(later);
}

let copiedTimer = null;
function showCopied(copied) {
  const button = $('diagnostics');
  button.textContent = t(copied ? 'system.copied' : 'system.copyRetry');
  button.dataset.state = copied ? 'done' : 'failed';
  $('diagnostics-result').textContent = t(copied ? 'system.copiedDone' : 'system.copyFailed');
  clearTimeout(copiedTimer);
  copiedTimer = setTimeout(() => {
    button.textContent = t('system.copy');
    delete button.dataset.state;
  }, 2000);
}

function set(key, value) {
  settings[key] = value;
  send({ type: 'set', key, value });
}

/* ------------------------------ helpers ------------------------------ */

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'style') Object.assign(node.style, value);
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children) node.append(child);
  return node;
}

/**
 * A row of role="radio" buttons with one tab stop and arrow-key navigation (WAI-ARIA radio group pattern).
 * Returns a function that marks the selected value.
 */
function radioGroup(container, options, onPick, render) {
  container.replaceChildren();
  const buttons = options.map((option, index) => {
    const button = el('button', { role: 'radio', 'aria-checked': 'false', tabindex: '-1' });
    render(button, option);
    button.addEventListener('click', () => onPick(option.value));
    button.addEventListener('keydown', event => {
      const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
      if (!step) return;
      event.preventDefault();
      const next = buttons[(index + step + buttons.length) % buttons.length];
      next.focus();
      next.click();
    });
    container.append(button);
    return button;
  });
  return selected => {
    buttons.forEach((button, index) => {
      const on = options[index].value === selected;
      button.setAttribute('aria-checked', String(on));
      button.tabIndex = on ? 0 : -1;
    });
  };
}

function bindSwitch(button, onToggle) {
  button.addEventListener('click', () => onToggle(button.getAttribute('aria-checked') !== 'true'));
}

function setSwitch(button, on) {
  button.setAttribute('aria-checked', String(!!on));
}

/* ------------------------------ build ------------------------------ */

let markSeconds, markTheme, markFont, markLock, markGame;
const viewSwitches = new Map();
const worldSelects = [];

function build() {
  // Views
  const views = $('views');
  views.replaceChildren();
  for (const view of model.views) {
    const name = t(`view.${view.id}.name`);
    const sw = el('button', { class: 'switch', role: 'switch', 'aria-label': name });
    bindSwitch(sw, on => {
      const next = model.views.map(v => v.id).filter(id => (id === view.id ? on : settings.views.includes(id)));
      if (next.length === 0) return; // at least one view stays on
      set('views', next);
      sync();
    });
    viewSwitches.set(view.id, sw);
    views.append(el('div', { class: 'row' }, [
      el('span', { class: 'row-text' }, [
        el('span', { class: 'row-title', text: name }),
        el('span', { class: 'row-desc', text: t(`view.${view.id}.desc`) })
      ]),
      sw
    ]));
  }

  markLock = radioGroup($('lock'), ['dim', 'off', 'keep'].map(value => ({ value, label: t(`system.lock.${value}`) })),
    value => { set('lockScreen', value); markLock(value); }, (button, option) => { button.textContent = option.label; });

  markGame = radioGroup($('game'), ['off', 'system', 'clock'].map(value => ({ value, label: t(`views.game.${value}`) })),
    value => { set('gameView', value); markGame(value); }, (button, option) => { button.textContent = option.label; });

  markSeconds = radioGroup($('seconds'), model.secondsChoices.map(s => ({ value: s, label: t('views.secondsValue', { n: s }) })),
    value => { set('viewSeconds', value); markSeconds(value); },
    (button, option) => { button.textContent = option.label; });

  // Themes: a chip painted with the theme's own background and accent
  markTheme = radioGroup($('themes'), model.themes.map(t => ({ ...t, value: t.id })),
    value => { set('theme', value); markTheme(value); },
    (button, theme) => {
      button.className = 'swatch';
      button.setAttribute('aria-label', theme.name);
      const stops = theme.stops.join(', ');
      button.append(
        el('span', {
          class: 'swatch-chip',
          style: { background: `linear-gradient(${theme.angle}deg, ${stops})`, '--chip-accent': theme.accent }
        }),
        el('span', { class: 'swatch-name', text: theme.name })
      );
      button.querySelector('.swatch-chip').style.setProperty('--chip-accent', theme.accent);
    });

  // Clock fonts: each button shows the digits in its own font
  markFont = radioGroup($('fonts'), model.fonts.map(f => ({ ...f, value: f.id })),
    value => { set('font', value); markFont(value); },
    (button, font) => {
      button.textContent = '12:30';
      button.title = font.name;
      button.setAttribute('aria-label', font.name);
      button.style.fontFamily = `"${font.family}", sans-serif`;
      button.style.fontStyle = font.italic ? 'italic' : 'normal';
      button.style.fontWeight = String(font.weight);
    });

  // Brightness: live label, changes sent at most every 120 ms while dragging
  const range = $('brightness');
  let pending = null;
  range.addEventListener('input', () => {
    showBrightness(+range.value);
    if (pending) return;
    pending = setTimeout(() => { pending = null; set('brightness', +range.value); }, 120);
  });
  range.addEventListener('change', () => set('brightness', +range.value));

  bindSwitch($('use24h'), on => { set('use24h', on); sync(); });

  // Dim at night: the times are sent once a full time is typed; the brightness like the main one
  bindSwitch($('night'), on => { set('nightDim', on); sync(); });
  for (const [id, key] of [['night-start', 'nightStart'], ['night-end', 'nightEnd']]) {
    $(id).addEventListener('change', event => { if (event.target.value) set(key, event.target.value); });
  }
  const night = $('night-brightness');
  let nightPending = null;
  night.addEventListener('input', () => {
    showNightBrightness(+night.value);
    if (nightPending) return;
    nightPending = setTimeout(() => { nightPending = null; set('nightBrightness', +night.value); }, 120);
  });
  night.addEventListener('change', () => set('nightBrightness', +night.value));
  $('diagnostics').addEventListener('click', () => send({ type: 'diagnostics' }));
  bindSwitch($('updates'), on => { set('checkUpdates', on); sync(); });
  bindSwitch($('approx'), on => { set('approxCpuTemp', on); sync(); });
  $('orientation').addEventListener('change', event => { set('orientation', event.target.value); });
  bindSwitch($('screen'), on => { setSwitch($('screen'), on); send({ type: 'screen', on }); });
  $('resume').addEventListener('click', () => send({ type: 'hold', view: null }));
  bindSwitch($('autostart'), on => { setSwitch($('autostart'), on); send({ type: 'autostart', value: on }); });
  bindSwitch($('admin'), on => { setSwitch($('admin'), on); send({ type: 'admin', value: on }); });

  // World clocks: three selects, "None" first
  const world = $('world');
  world.replaceChildren();
  worldSelects.length = 0;
  for (let slot = 0; slot < model.worldMax; slot++) {
    const select = el('select', { class: 'select', 'aria-label': t('world.slot', { n: slot + 1 }) }, [
      el('option', { value: '', text: t('world.none') }),
      ...model.worldCities.map(c => el('option', { value: c.timeZoneId, text: c.label }))
    ]);
    select.addEventListener('change', () => {
      set('worldClocks', worldSelects.map(s => s.value).filter(Boolean));
      sync();
    });
    worldSelects.push(select);
    world.append(select);
  }

  buildDesigns();
  buildCitySearch();
}

/* ------------------------------ sync from settings ------------------------------ */

function sync() {
  if (!model || !settings) return;
  for (const [id, sw] of viewSwitches) {
    const on = settings.views.includes(id);
    setSwitch(sw, on);
    // the last view on cannot be switched off: it stays fully visible, and says why
    const last = on && settings.views.length === 1;
    sw.title = last ? t('views.lastOn') : '';
    sw.setAttribute('aria-disabled', String(last));
  }
  markSeconds(settings.viewSeconds);
  markTheme(settings.theme);
  markFont(settings.font);
  // never move a control the user is dragging: the app's answer can be a step behind the pointer
  if (document.activeElement !== $('brightness')) $('brightness').value = settings.brightness;
  showBrightness(+$('brightness').value);
  setSwitch($('use24h'), settings.use24h);
  setSwitch($('updates'), settings.checkUpdates);
  setSwitch($('approx'), settings.approxCpuTemp);
  $('orientation').value = settings.orientation;
  document.body.classList.toggle('portrait', settings.orientation.startsWith('portrait'));
  for (const img of [$('preview'), ...document.querySelectorAll('.design-thumb')]) {
    img.width = settings.orientation.startsWith('portrait') ? 320 : 480;
    img.height = settings.orientation.startsWith('portrait') ? 480 : 320;
  }
  setSwitch($('night'), settings.nightDim);
  $('night-options').hidden = !settings.nightDim;
  for (const [id, key] of [['night-start', 'nightStart'], ['night-end', 'nightEnd']]) {
    if (document.activeElement !== $(id)) $(id).value = settings[key];
  }
  if (document.activeElement !== $('night-brightness')) $('night-brightness').value = settings.nightBrightness;
  showNightBrightness(+$('night-brightness').value);
  markLock(settings.lockScreen);
  markGame(settings.gameView);
  syncDesign();

  worldSelects.forEach((select, slot) => {
    select.value = settings.worldClocks[slot] || '';
    select.classList.toggle('empty', !select.value);
  });

  const current = $('city-current');
  current.replaceChildren();
  if (settings.weatherCity) {
    const city = settings.weatherCity;
    const place = el('span', {}, [el('strong', { text: city.name }), city.region ? `, ${city.region}` : '']);
    current.append(...tNodes('weather.showing', { city: place }));
  } else {
    current.textContent = t('weather.none');
  }
}

function showBrightness(value) {
  $('brightness-value').textContent = `${value}%`;
  $('brightness').style.setProperty('--fill', `${value}%`);
}

function showNightBrightness(value) {
  $('night-brightness-value').textContent = `${value}%`;
  $('night-brightness').style.setProperty('--fill', `${value}%`);
}

function syncSystem(system) {
  if (!system) return;
  setSwitch($('autostart'), system.autostart);
  setSwitch($('admin'), system.admin);
  // Installed for one user only, Vigil does not start with administrator rights without asking: any program could
  // change its files (Installation.ForAllUsers in Settings.cs).
  $('admin-desc').textContent = t(system.admin && system.elevated
    ? (system.allUsers ? 'system.admin.on' : 'system.admin.onEachStart')
    : system.admin
      ? 'system.admin.denied'
      : system.allUsers ? 'system.admin.off' : 'system.admin.offEachStart');
}

/* ------------------------------ system design ------------------------------ */

let markDesign;
const colorInputs = { accent: 'color-accent', secondary: 'color-secondary', alert: 'color-alert' };

function buildDesigns() {
  markDesign = radioGroup($('designs'), model.designs.map(d => ({ ...d, value: d.id })),
    value => { set('systemDesign', value); syncDesign(); },
    (button, design) => {
      button.className = 'design';
      const name = t(`design.${design.id}.name`);
      button.setAttribute('aria-label', name + '. ' + t(`design.${design.id}.desc`));
      button.append(
        el('img', { class: 'design-thumb', alt: '', width: '480', height: '320', 'data-design': design.id }),
        el('span', { class: 'design-name', text: name }));
    });

  for (const [key, id] of Object.entries(colorInputs)) {
    $(id).addEventListener('input', () => queueStyle({ [key]: $(id).value.toUpperCase() }));
  }
  const alertTemp = $('alert-temp');
  alertTemp.min = model.alertTempRange.min;
  alertTemp.max = model.alertTempRange.max;
  alertTemp.addEventListener('input', () => queueStyle({ alertTemp: +alertTemp.value }));
  $('reset-colors').addEventListener('click', resetStyle);

  // Custom design: one select per card
  const tiles = $('tiles');
  tiles.replaceChildren();
  tileSelects.length = 0;
  for (let slot = 0; slot < 6; slot++) {
    const select = el('select', { class: 'select', 'aria-label': t('design.tile', { n: slot + 1 }) },
      model.metrics.map(m => el('option', { value: m.id, text: t(`metric.${m.id}`) })));
    select.addEventListener('change', () => {
      settings.customTiles = tileSelects.map(s => s.value);
      set('customTiles', settings.customTiles);
    });
    tileSelects.push(select);
    tiles.append(select);
  }
}

const tileSelects = [];

const styleOf = design => (settings.designs && settings.designs[design]) || {};

/** The colours the user changed for the design on show, sent once they stop (a colour picker fires on every drag). */
let styleTimer = null;
function queueStyle(patch) {
  const design = settings.systemDesign;
  const merged = { ...styleOf(design), ...patch };
  settings.designs = { ...settings.designs, [design]: merged };
  showAlertTemp(+$('alert-temp').value);
  $('reset-colors').disabled = false;
  clearTimeout(styleTimer);
  styleTimer = setTimeout(() => send({ type: 'set', key: 'designStyle', value: { design, ...merged } }), 150);
}

function resetStyle() {
  clearTimeout(styleTimer);
  const design = settings.systemDesign;
  const designs = { ...settings.designs };
  delete designs[design];
  settings.designs = designs;
  send({ type: 'set', key: 'designStyle', value: { design, accent: null, secondary: null, alert: null, alertTemp: null } });
  syncDesign();
}

function showAlertTemp(value) {
  const range = $('alert-temp');
  $('alert-temp-value').textContent = value + ' °C';
  range.style.setProperty('--fill', ((value - range.min) / (range.max - range.min)) * 100 + '%');
}

function syncDesign() {
  if (!model || !settings) return;
  markDesign(settings.systemDesign);
  const info = model.designs.find(d => d.id === settings.systemDesign);
  $('design-desc').textContent = info ? t(`design.${info.id}.desc`) : '';
  $('tiles-field').hidden = settings.systemDesign !== 'custom';
  tileSelects.forEach((select, slot) => {
    if (document.activeElement !== select) select.value = settings.customTiles[slot];
  });

  const own = styleOf(settings.systemDesign);
  const defaults = settings.styleDefaults;
  for (const [key, id] of Object.entries(colorInputs)) {
    const input = $(id);
    if (document.activeElement !== input) input.value = (own[key] || defaults[key]).toLowerCase();
  }
  const range = $('alert-temp');
  if (document.activeElement !== range) range.value = own.alertTemp != null ? own.alertTemp : defaults.alertTemp;
  showAlertTemp(+range.value);
  $('reset-colors').disabled = !Object.values(own).some(v => v != null);
}

/* ------------------------------ status and preview ------------------------------ */

function showStatus(state, text) {
  $('status').dataset.state = state;
  $('status-text').textContent = text;
}

/* ------------------------------ quick controls ------------------------------ */

const viewName = id => t(`view.${id}.short`);

let nowFor = '';
let markNow = null;

/** Under the preview: the views that take turns (a click keeps one on), the brightness note and the screen switch. */
function showControls(frame) {
  if (!model || !settings) return;

  // One button per view switched on, rebuilt only when that list changes.
  const key = settings.views.join();
  if (key !== nowFor) {
    nowFor = key;
    markNow = radioGroup($('now'), settings.views.map(id => ({ value: id })),
      id => send({ type: 'hold', view: id }),
      (button, option) => { button.textContent = viewName(option.value); });
  }
  markNow(frame.view);
  const ready = new Set(frame.ready);
  [...$('now').children].forEach((button, index) => {
    const waiting = !ready.has(settings.views[index]);
    button.disabled = waiting;
    button.title = waiting ? t('controls.waiting') : '';
  });

  const now = $('now');
  now.dataset.held = String(!!frame.held);
  $('resume').hidden = !frame.held;
  $('rotation').textContent =
    !frame.screenOn ? t('controls.off')
    : frame.gaming ? t('controls.gaming', { view: viewName(frame.view) })
    : frame.held ? t('controls.held', { view: viewName(frame.held) })
    : frame.next ? t('controls.next', { view: viewName(frame.next), seconds: frame.nextIn })
    : settings.views.length > 1 ? t('controls.othersWaiting')
    : t('controls.single');

  setSwitch($('screen'), frame.screenOn);
  document.querySelector('.preview').dataset.off = String(!frame.screenOn);

  // At night the backlight may be lower than the slider says: tell, so the slider does not look broken.
  const dimmed = frame.night && settings.nightBrightness < settings.brightness;
  $('brightness-note').hidden = !dimmed;
  if (dimmed) $('brightness-note').textContent = t('controls.nightDimmed', { value: settings.nightBrightness, time: settings.nightEnd });
}

/* ------------------------------ city search ------------------------------ */

let searchId = 0;
let results = [];
let active = -1;

function buildCitySearch() {
  const input = $('city');
  let timer = null;

  input.addEventListener('input', () => {
    clearTimeout(timer);
    const name = input.value.trim();
    if (name.length < 2) { closeResults(); return; }
    timer = setTimeout(() => send({ type: 'searchCity', id: ++searchId, name }), 350);
  });

  input.addEventListener('keydown', event => {
    const list = $('city-results');
    if (event.key === 'Escape') { closeResults(); return; }
    if (list.hidden || results.length === 0) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      active = (active + (event.key === 'ArrowDown' ? 1 : -1) + results.length) % results.length;
      markActive();
    } else if (event.key === 'Enter' && active >= 0) {
      event.preventDefault();
      pick(results[active]);
    }
  });

  input.addEventListener('blur', () => setTimeout(closeResults, 150));
}

function showCityResults(message) {
  if (message.id !== searchId) return; // an older search answered late
  const list = $('city-results');
  results = message.cities || [];
  active = results.length ? 0 : -1;
  list.replaceChildren();
  if (message.error) {
    list.append(el('li', { class: 'result-empty', text: t('weather.searchFailed') }));
  } else if (results.length === 0) {
    list.append(el('li', { class: 'result-empty', text: t('weather.noMatch') }));
  }
  results.forEach((city, index) => {
    list.append(el('li', {
      id: `city-${index}`,
      role: 'option',
      'aria-selected': 'false',
      onmousedown: event => { event.preventDefault(); pick(city); },
      onmousemove: () => { active = index; markActive(); }
    }, [
      el('span', { class: 'result-name', text: city.name }),
      el('span', { class: 'result-region', text: city.region || '' })
    ]));
  });
  list.hidden = false;
  $('city').setAttribute('aria-expanded', 'true');
  markActive();
}

function markActive() {
  [...$('city-results').querySelectorAll('[role="option"]')].forEach((item, index) => {
    item.setAttribute('aria-selected', String(index === active));
  });
  $('city').setAttribute('aria-activedescendant', active >= 0 ? `city-${active}` : '');
}

function pick(city) {
  set('weatherCity', city);
  $('city').value = '';
  closeResults();
  sync();
}

function closeResults() {
  $('city-results').hidden = true;
  $('city').setAttribute('aria-expanded', 'false');
  results = [];
  active = -1;
}
