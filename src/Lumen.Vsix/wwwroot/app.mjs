import pt from './locales/pt-BR.json' with { type: 'json' };
import en from './locales/en.json' with { type: 'json' };
import { HostBridge, MAX_TEXT_LENGTH } from './bridge.mjs';
import { createMarkdownRenderer, renderDiff } from './renderers.mjs';
import { VirtualHistory, ROW_HEIGHT } from './virtual-history.mjs';
import { themeProperties } from './theme.mjs';

const catalogs = { 'pt-BR': pt, en };
const element = id => document.getElementById(id);
const input = element('message-input');
const send = element('send');
const viewport = element('history-viewport');
const markdown = createMarkdownRenderer();
const requestedCulture = new URL(location.href).searchParams.get('culture');
let culture = (requestedCulture || navigator.language).toLowerCase().startsWith('pt') ? 'pt-BR' : 'en';
let state = 'preview';
let busy = false;
let errorCode = null;
let bridge;
const messages = Array.from({ length: 500 }, (_, index) => ({ kind: 'sample', index }));

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

function renderMessage(message, index) {
  const row = document.createElement('article');
  row.className = 'message';
  row.dataset.kind = message.kind;
  const header = document.createElement('div');
  header.className = 'message-header';
  const author = document.createElement('strong');
  const number = document.createElement('span');
  number.className = 'message-number';
  number.textContent = text('messageNumber', { number: index + 1 });
  const body = document.createElement('p');
  body.className = 'message-text';
  if (message.kind === 'sample') {
    const question = message.index % 2 === 0;
    author.textContent = text(question ? 'sampleUser' : 'sampleAssistant');
    body.textContent = text(question ? 'sampleQuestion' : 'sampleAnswer');
  } else {
    author.textContent = text(message.kind === 'user' ? 'userRole' : 'hostRole');
    body.textContent = message.text;
  }
  header.append(author, number);
  row.append(header, body);
  return row;
}

const history = new VirtualHistory(viewport, element('history-spacer'), element('history-items'), renderMessage, (range, count) => {
  element('history-range').textContent = text('historyRange', { start: range.start + 1, end: range.end, total: count });
});

function updateControls() {
  input.disabled = false;
  input.readOnly = busy;
  send.disabled = state !== 'connected' || busy || !input.value.trim() || input.value.length > MAX_TEXT_LENGTH;
  send.textContent = text(busy ? 'sending' : 'send');
  element('character-count').textContent = text('composerCount', { count: input.value.length, max: MAX_TEXT_LENGTH });
  element('connection-status').textContent = text(`${state}Status`);
  element('connection-status').dataset.state = state;
  element('connection-detail').textContent = text(`${state}Detail`);
  element('retry').hidden = state !== 'failed' || !bridge?.available;
  element('composer').setAttribute('aria-busy', String(busy));
}

function growComposer() {
  input.style.height = '0px';
  const height = input.scrollHeight + 2;
  input.style.height = `${Math.min(height, 144)}px`;
  input.style.overflowY = height > 144 ? 'auto' : 'hidden';
  updateControls();
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
  element('markdown-sample').innerHTML = markdown.render(text('sampleMarkdown'));
  for (const block of element('markdown-sample').querySelectorAll('pre')) {
    block.tabIndex = 0;
    block.setAttribute('aria-label', text('codeLabel'));
  }
  element('diff-sample').innerHTML = renderDiff(text('sampleDiff'), text('diffEmpty'));
  element('diff-sample').querySelectorAll('table').forEach((table, index) => {
    table.setAttribute('aria-label', text(index === 0 ? 'diffBefore' : 'diffAfter'));
  });
  history.setItems(messages);
  updateControls();
  if (errorCode) showError(errorCode);
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
}

async function submit(event) {
  event.preventDefault();
  if (send.disabled) return;
  const value = input.value;
  busy = true;
  clearError();
  element('announcement').textContent = '';
  updateControls();
  try {
    const result = await bridge.request('ping', { text: value });
    messages.push({ kind: 'user', text: value }, { kind: 'host', text: result.text });
    history.setItems(messages);
    viewport.scrollTop = messages.length * ROW_HEIGHT;
    history.render();
    input.value = '';
    element('announcement').textContent = text('echoReceived');
  } catch (error) {
    showError(error.code);
  } finally {
    busy = false;
    growComposer();
    input.focus();
  }
}

function handleLink(event) {
  const anchor = event.target.closest('a');
  if (!anchor) return;
  event.preventDefault();
  if (anchor.getAttribute('href') === '#section') {
    if (event.type === 'click') viewport.focus({ preventScroll: true });
  } else {
    element('announcement').textContent = text('externalLinkBlocked');
  }
}

bridge = new HostBridge(window.chrome?.webview, { onTheme: applyHostTheme, onError: showError });
applyLocale(culture);
growComposer();
document.querySelector('.app').setAttribute('aria-busy', 'false');
element('composer').addEventListener('submit', submit);
input.addEventListener('input', growComposer);
input.addEventListener('keydown', event => {
  if (event.key === 'Enter' && event.ctrlKey && !event.isComposing) {
    event.preventDefault();
    if (!send.disabled) element('composer').requestSubmit();
  }
});
viewport.addEventListener('keydown', event => {
  if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault();
    history.jump(event.key === 'End');
  }
});
element('first-message').addEventListener('click', () => history.jump(false));
element('last-message').addEventListener('click', () => history.jump(true));
element('retry').addEventListener('click', connect);
for (const eventName of ['click', 'auxclick', 'contextmenu']) document.addEventListener(eventName, handleLink);
const composerResize = new ResizeObserver(growComposer);
composerResize.observe(element('composer'));
window.addEventListener('pagehide', () => {
  bridge.dispose();
  history.dispose();
  composerResize.disconnect();
}, { once: true });
if (bridge.available) void connect();
