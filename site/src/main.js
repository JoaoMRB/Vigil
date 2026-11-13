// The page is complete without this file, only still. With it, the screens come alive: the hero screen switches on,
// the readings move like real sensors, the clocks keep real time, and sections draw themselves in as they are
// reached. With "reduce motion" on, only the clocks change.

const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
const finePointer = matchMedia('(pointer: fine)').matches;
const lang = document.documentElement.lang || 'en';
const all = selector => document.querySelectorAll(selector);

// ---------- Sections: reveal once, the first time each one comes into view ----------

const onReveal = new Map();   // section → what to start when it appears
const sections = all('.reveal');
function reveal(section) {
  section.classList.add('is-visible');
  onReveal.get(section)?.forEach(start => start());
}
if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      reveal(entry.target);
      observer.unobserve(entry.target);
    }
  }, { rootMargin: '0px 0px -10% 0px', threshold: 0.08 });
  sections.forEach(section => observer.observe(section));
} else {
  sections.forEach(reveal);
}
function whenRevealed(element, start) {
  const section = element.closest('.reveal');
  if (!section || section.classList.contains('is-visible')) return start();
  onReveal.set(section, [...(onReveal.get(section) ?? []), start]);
}

// ---------- Clocks: the visitor's own time, and the real time in the world clocks ----------

const two = n => String(n).padStart(2, '0');
// Weekday, day and month are formatted one by one: together, some languages (Portuguese) turn them into "13/11".
const shortDate = [{ weekday: 'short' }, { day: 'numeric' }, { month: 'short' }].map(part => new Intl.DateTimeFormat(lang, part));
const longDate = [{ weekday: 'long' }, { day: 'numeric', month: 'long' }].map(part => new Intl.DateTimeFormat(lang, part));
const zones = [...all('[data-zone]')].map(element => ({
  element,
  format: new Intl.DateTimeFormat('en-GB', { timeZone: element.dataset.zone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
}));

function tick() {
  const now = new Date();
  const time = `${two(now.getHours())}:${two(now.getMinutes())}`;
  for (const el of all('[data-clock-h]')) el.textContent = two(now.getHours());
  for (const el of all('[data-clock-m]')) el.textContent = two(now.getMinutes());
  for (const el of all('[data-clock-date]')) el.textContent = shortDate.map(f => f.format(now).replace(/[.,]/g, '')).join(' ');
  for (const el of all('[data-time]')) el.textContent = time;
  for (const el of all('[data-long-date]')) el.textContent = longDate.map(f => f.format(now)).join(' · ');
  for (const { element, format } of zones) element.textContent = format.format(now);
  // Wake up just after the next minute starts rather than polling every second.
  setTimeout(tick, 60_000 - (now.getSeconds() * 1000 + now.getMilliseconds()) + 50);
}
tick();

// ---------- Live readings: one set of sensors behind every screen on the page ----------

// Each reading wanders around its usual value, as a PC at rest does. Same digit count throughout, so nothing shifts.
const sensors = {
  cpu:  { value: 41,  base: 41,  step: 2.4, min: 36,  max: 54,  digits: 0 },
  gpu:  { value: 38,  base: 38,  step: 1.6, min: 34,  max: 47,  digits: 0 },
  ram:  { value: 34,  base: 34,  step: 0.8, min: 32,  max: 37,  digits: 0 },
  load: { value: 23,  base: 23,  step: 10,  min: 10,  max: 64,  digits: 0 },
  net:  { value: 4.1, base: 4.1, step: 2.6, min: 0.4, max: 9.8, digits: 1 }
};

// Graphs keep their last points and scroll left as new readings come in.
const graphs = [...all('[data-graph]')].map(line => ({
  line,
  key: line.dataset.graph,
  ys: line.getAttribute('points').trim().split(/\s+/).map(point => Number(point.split(',')[1]))
}));
const graphY = { load: v => 48 - v * 0.62, net: v => 47 - v * 4 };

function step() {
  if (document.hidden) return;
  for (const [key, s] of Object.entries(sensors)) {
    s.value += (Math.random() - 0.5) * s.step + (s.base - s.value) * 0.18;
    s.value = Math.min(s.max, Math.max(s.min, s.value));
    const text = s.value.toFixed(s.digits);
    for (const el of all(`[data-live="${key}"]`)) el.textContent = text;
  }
  for (const graph of graphs) {
    graph.ys.shift();
    graph.ys.push(Math.round(graphY[graph.key](sensors[graph.key].value) * 10) / 10);
    graph.line.setAttribute('points', graph.ys.map((y, i) => `${i * 300 / (graph.ys.length - 1)},${y}`).join(' '));
  }
}
if (!still) setInterval(step, 2000);

// ---------- Hero: the device leans a little towards the pointer ----------

const hero = document.querySelector('.hero');
const frame = document.querySelector('.device-frame');
if (!still && finePointer && hero && frame) {
  let pending = null;
  hero.addEventListener('pointermove', event => {
    pending = event;
    requestAnimationFrame(() => {
      if (!pending) return;
      const box = frame.getBoundingClientRect();
      const x = Math.max(-1, Math.min(1, (pending.clientX - (box.left + box.width / 2)) / (box.width / 2)));
      const y = Math.max(-1, Math.min(1, (pending.clientY - (box.top + box.height / 2)) / box.height));
      frame.style.setProperty('--ry', `${(x * 4).toFixed(2)}deg`);
      frame.style.setProperty('--rx', `${(-y * 2.5).toFixed(2)}deg`);
      pending = null;
    });
  });
  hero.addEventListener('pointerleave', () => {
    frame.style.setProperty('--ry', '0deg');
    frame.style.setProperty('--rx', '0deg');
  });
}

// ---------- Sensors: values count up the first time they are seen ----------

if (!still) {
  for (const element of all('[data-count]')) {
    const final = element.textContent;
    const numbers = final.match(/\d+(\.\d+)?/g) ?? [];
    // Each number keeps its width while counting (figure spaces are as wide as digits), so the row never shifts.
    const frameAt = progress => {
      let i = 0;
      return final.replace(/\d+(\.\d+)?/g, match => {
        const digits = (match.split('.')[1] ?? '').length;
        return (Number(numbers[i++]) * progress).toFixed(digits).padStart(match.length, ' ');
      });
    };
    element.textContent = frameAt(0);
    whenRevealed(element, () => {
      const start = performance.now();
      const run = now => {
        const t = Math.min(1, (now - start) / 1100);
        element.textContent = t < 1 ? frameAt(1 - (1 - t) ** 3) : final;
        if (t < 1) requestAnimationFrame(run);
      };
      requestAnimationFrame(run);
    });
  }
}

// ---------- Statement: words light up one by one as it scrolls past ----------

const statement = document.querySelector('[data-words]');
if (!still && statement) {
  const words = [];
  const wrap = node => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === Node.ELEMENT_NODE) { wrap(child); continue; }
      if (child.nodeType !== Node.TEXT_NODE || !child.textContent.trim()) continue;
      const fragment = document.createDocumentFragment();
      for (const part of child.textContent.split(/(\s+)/)) {
        if (!part) continue;
        if (/^\s+$/.test(part)) { fragment.append(part); continue; }
        const word = document.createElement('span');
        word.className = 'w';
        word.textContent = part;
        fragment.append(word);
        words.push(word);
      }
      child.replaceWith(fragment);
    }
  };
  wrap(statement);
  statement.classList.add('is-lighting');

  let queued = false;
  const update = () => {
    queued = false;
    const box = statement.getBoundingClientRect();
    const view = innerHeight;
    // From the moment the text is near the bottom of the screen until its last line passes the middle.
    const progress = (view * 0.9 - box.top) / (view * 0.35 + box.height);
    const lit = Math.round(Math.max(0, Math.min(1, progress)) * words.length);
    words.forEach((word, i) => word.classList.toggle('is-lit', i < lit));
  };
  addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(update); } }, { passive: true });
  addEventListener('resize', update);
  update();
}

// ---------- Language menu: <details> opens and closes by itself; this adds Esc and a click elsewhere ----------

const languageMenu = document.querySelector('details.lang');
if (languageMenu) {
  document.addEventListener('click', event => {
    if (languageMenu.open && !languageMenu.contains(event.target)) languageMenu.open = false;
  });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || !languageMenu.open) return;
    languageMenu.open = false;
    languageMenu.querySelector('summary').focus();
  });
}

// ---------- Web fonts: requested only after the first frame is on screen, so the page never waits for them ----------

// The address is the one in the <noscript data-fonts> fallback, kept in a single place.
const fontsTag = document.querySelector('noscript[data-fonts]');
const fontsHref = fontsTag?.textContent.match(/href="([^"]+)"/)?.[1].replaceAll('&amp;', '&');
if (fontsHref) {
  requestAnimationFrame(() => setTimeout(() => {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = fontsHref;
    document.head.append(link);
  }, 0));
}
