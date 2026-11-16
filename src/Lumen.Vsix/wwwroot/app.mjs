import pt from './locales/pt-BR.json' with { type: 'json' };
import en from './locales/en.json' with { type: 'json' };
import { HostBridge } from './bridge.mjs';
import { createMarkdownRenderer } from './renderers.mjs';
import { applyPreset } from './theme.mjs';
import { startByok } from './byok.mjs';
import { startThemeEditor } from './theme-editor.mjs';

const catalogs = { 'pt-BR': pt, en };
const element = id => document.getElementById(id);
const markdown = createMarkdownRenderer();
const requestedCulture = new URL(location.href).searchParams.get('culture');
let culture = (requestedCulture || navigator.language).toLowerCase().startsWith('pt') ? 'pt-BR' : 'en';
let state = 'preview';
let errorCode = null;
let bridge;
let byokUI;
let themeEditor;
let vsTheme = { background: '#131318', foreground: '#ececf2', accent: '#42c99a' };
let customTheme = null;

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
  themeEditor?.refresh();
}

function applyHostTheme(theme) {
  vsTheme = { background: theme.background, foreground: theme.foreground, accent: theme.accent };
  applyPreset(customTheme, vsTheme);
  themeEditor?.refresh();
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
  if (state === 'connected') void themeEditor.load();
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
themeEditor = startThemeEditor({ document, bridge: byokUI.bridge, text, hostTheme: () => vsTheme,
  display: value => { customTheme = value; applyPreset(value, vsTheme); } });
document.querySelector('.app').setAttribute('aria-busy', 'false');
element('retry').addEventListener('click', connect);
element('byok-skip-link')?.addEventListener('click', () => {
  const target = element('byok-message').disabled ? element('byok-output') : element('byok-message');
  target?.focus({ preventScroll: true });
});

const workspacePanels = [...document.querySelectorAll('.workspace-content > details')];
const railButtons = [...document.querySelectorAll('.rail-button')];
const workspaceTabs = [...document.querySelectorAll('.workspace-tab')];
const chatMain = document.querySelector('.chat-main');
const composerPanel = document.querySelector('.composer-panel');
let workspaceTrigger = null;

// Visibility is driven by CSS :has(details[open]) so byok.mjs can open a panel and focus it in the same task.
function syncWorkspace() {
  const activePanel = workspacePanels.find(panel => panel.open);
  chatMain.inert = Boolean(activePanel);
  composerPanel.inert = Boolean(activePanel);
  for (const button of railButtons) button.setAttribute('aria-expanded', String(element(button.dataset.panel).open));
  for (const tab of workspaceTabs) tab.setAttribute('aria-pressed', String(tab.dataset.panel === activePanel?.id));
}

function selectWorkspacePanel(panel, trigger) {
  for (const other of workspacePanels) other.open = other === panel;
  workspaceTrigger = trigger;
  syncWorkspace();
  panel.querySelector('summary').focus({ preventScroll: true });
}

function closeWorkspace() {
  for (const panel of workspacePanels) panel.open = false;
  syncWorkspace();
  (workspaceTrigger || railButtons[0]).focus({ preventScroll: true });
  workspaceTrigger = null;
}

for (const button of railButtons) {
  button.addEventListener('click', () => {
    const panel = element(button.dataset.panel);
    if (panel.open) closeWorkspace();
    else selectWorkspacePanel(panel, button);
  });
}
for (const tab of workspaceTabs) tab.addEventListener('click', () => {
  const trigger = railButtons.find(button => button.dataset.panel === tab.dataset.panel);
  selectWorkspacePanel(element(tab.dataset.panel), trigger);
});
for (const panel of workspacePanels) panel.addEventListener('toggle', () => {
  if (panel.open) for (const other of workspacePanels) if (other !== panel) other.open = false;
  syncWorkspace();
});
element('lumen-workspace-close').addEventListener('click', closeWorkspace);
document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' || !element('byok-history-confirmation').hidden) return;
  if (!workspacePanels.some(panel => panel.open)) return;
  event.preventDefault();
  closeWorkspace();
});
syncWorkspace();

for (const eventName of ['click', 'auxclick', 'contextmenu']) document.addEventListener(eventName, handleLink);
window.addEventListener('pagehide', () => {
  bridge.dispose();
  byokUI.dispose();
}, { once: true });
if (bridge.available) void connect();
