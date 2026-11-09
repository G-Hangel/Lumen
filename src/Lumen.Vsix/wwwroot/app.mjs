import pt from './locales/pt-BR.json' with { type: 'json' };
import en from './locales/en.json' with { type: 'json' };
import { HostBridge } from './bridge.mjs';
import { createMarkdownRenderer } from './renderers.mjs';
import { themeProperties } from './theme.mjs';
import { startByok } from './byok.mjs';

const catalogs = { 'pt-BR': pt, en };
const element = id => document.getElementById(id);
const markdown = createMarkdownRenderer();
const requestedCulture = new URL(location.href).searchParams.get('culture');
let culture = (requestedCulture || navigator.language).toLowerCase().startsWith('pt') ? 'pt-BR' : 'en';
let state = 'preview';
let errorCode = null;
let bridge;
let byokUI;

function text(key, values = {}) {
  return catalogs[culture][key].replace(/\{(\w+)\}/g, (match, name) => Object.hasOwn(values, name) ? String(values[name]) : match);
}

function showError(code) {
  errorCode = code;
  const key = Object.hasOwn(catalogs[culture], `error_${code}`) ? `error_${code}` : 'error_unknown';
  element('error').textContent = text(key);
  element('error').hidden = false;
}

function clearError() {
  errorCode = null;
  element('error').hidden = true;
  element('error').textContent = '';
}

function updateControls() {
  element('connection-status').textContent = text(`${state}Status`);
  element('connection-status').dataset.state = state;
  document.querySelector('.composer-panel').dataset.connected = String(state === 'connected');
  element('connection-detail').textContent = text(`${state}Detail`);
  element('retry').hidden = state !== 'failed' || !bridge?.available;
}

function applyLocale(nextCulture) {
  culture = nextCulture;
  document.documentElement.lang = culture;
  document.title = text('appTitle');
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = text(node.dataset.i18n);
  for (const [attribute, dataKey] of [['aria-label', 'i18nLabel'], ['title', 'i18nTitle'], ['placeholder', 'i18nPlaceholder']]) {
    for (const node of document.querySelectorAll(`[data-${dataKey.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}]`)) {
      node.setAttribute(attribute, text(node.dataset[dataKey]));
    }
  }
  updateControls();
  if (errorCode) showError(errorCode);
  byokUI?.refreshLocale();
}

function applyHostTheme(theme) {
  for (const [property, value] of Object.entries(themeProperties(theme))) document.documentElement.style.setProperty(property, value);
  if (theme.culture && theme.culture !== culture) applyLocale(theme.culture);
}

async function connect() {
  if (!bridge.available || state === 'connecting') return;
  state = 'connecting';
  clearError();
  updateControls();
  try {
    const result = await bridge.request('ready', {});
    applyHostTheme(result);
    state = 'connected';
  } catch (error) {
    state = 'failed';
    showError(error.code);
  }
  updateControls();
  byokUI?.setConnected();
}

function handleLink(event) {
  const anchor = event.target.closest('a');
  if (!anchor) return;
  event.preventDefault();
  element('announcement').textContent = text('externalLinkBlocked');
}

bridge = new HostBridge(window.chrome?.webview, { onTheme: applyHostTheme, onError: showError });
byokUI = startByok({ transport: window.chrome?.webview, connected: () => state === 'connected', text, renderMarkdown: source => markdown.render(source) });
applyLocale(culture);
document.querySelector('.app').setAttribute('aria-busy', 'false');
element('retry').addEventListener('click', connect);
element('byok-skip-link')?.addEventListener('click', () => {
  const target = element('byok-message').disabled ? element('byok-output') : element('byok-message');
  target?.focus({ preventScroll: true });
});
for (const eventName of ['click', 'auxclick', 'contextmenu']) document.addEventListener(eventName, handleLink);
window.addEventListener('pagehide', () => {
  bridge.dispose();
  byokUI.dispose();
}, { once: true });
if (bridge.available) void connect();
