// Small things, nothing else moves: sections rise in as they are reached, the hero screen shows the visitor's own
// time, and the language menu closes like a menu should. Without this file the page is complete, only still.

// Sections: reveal once, the first time each one comes into view.
const sections = document.querySelectorAll('.reveal');
if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add('is-visible');
      observer.unobserve(entry.target);
    }
  }, { rootMargin: '0px 0px -10% 0px', threshold: 0.08 });
  sections.forEach(section => observer.observe(section));
} else {
  sections.forEach(section => section.classList.add('is-visible'));
}

// Hero clock: the visitor's local time, 24-hour like the screen in the picture. The date is written in the page's
// language so it sits well next to the rest of the screen ("London 14°").
const hours = document.querySelector('[data-clock-h]');
const minutes = document.querySelector('[data-clock-m]');
const date = document.querySelector('[data-clock-date]');
// Weekday, day and month are formatted one by one: together, some languages (Portuguese) turn them into "13/11".
const lang = document.documentElement.lang || 'en';
const dateParts = [{ weekday: 'short' }, { day: 'numeric' }, { month: 'short' }].map(part => new Intl.DateTimeFormat(lang, part));

function tick() {
  const now = new Date();
  hours.textContent = String(now.getHours()).padStart(2, '0');
  minutes.textContent = String(now.getMinutes()).padStart(2, '0');
  date.textContent = dateParts.map(format => format.format(now).replace(/[.,]/g, '')).join(' ');
  // Wake up just after the next minute starts rather than polling every second.
  setTimeout(tick, 60_000 - (now.getSeconds() * 1000 + now.getMilliseconds()) + 50);
}

if (hours && minutes && date) tick();

// Language menu: a <details> element opens and closes by itself; this closes it with Esc or a click elsewhere.
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

// Web fonts: requested only after the first frame is on screen, so the page never waits for them. The address is the
// one in the <noscript data-fonts> fallback, kept in a single place.
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
