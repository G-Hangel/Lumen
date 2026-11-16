import { isThemePreset } from './byok-bridge.mjs';
import { contrast } from './theme.mjs';
import { bundledThemes } from './theme-presets.mjs';

const fields = ['background', 'foreground', 'accent', 'chatFont', 'codeFont', 'fontSize', 'density', 'borderRadius'];
const modes = ['ask', 'plan', 'agent'];
export const MAX_THEME_FILE_BYTES = 16384;

export function parseThemeFile(contents) {
  const result = JSON.parse(contents.replace(/^\uFEFF/, ''));
  if (result === null || !isThemePreset(result)) throw new Error('invalid_theme');
  return result;
}

export function themeWarnings(preset, host) {
  const background = preset?.background ?? host.background;
  const foreground = preset?.foreground ?? host.foreground;
  const accent = preset?.accent ?? host.accent;
  return contrast(foreground, background) < 4.5 || contrast(accent, background) < 3 ||
    modes.some(mode => preset?.modeAccent?.[mode] && contrast(preset.modeAccent[mode], background) < 3);
}

export function startThemeEditor({ document, bridge, text, hostTheme, display }) {
  const node = id => document.getElementById(`theme-${id}`);
  const form = node('form'), panel = node('panel');
  let saved = null, preview = null, ready = false, busy = false, valid = true, status = '';
  let draftName = null, importGeneration = 0;
  const inputs = [...fields, ...modes].map(node);

  function render() {
    node('apply').disabled = !ready || busy || !valid;
    node('reset').disabled = busy || !bridge.isConnected();
    node('cancel').disabled = busy;
    node('preset').disabled = busy || !ready;
    node('import').disabled = busy || !ready;
    node('export').disabled = busy || !ready || !valid;
    for (const input of inputs) input.disabled = busy || !ready;
    node('status').textContent = status ? text(status) : '';
    node('warning').textContent = !valid ? text('themeInvalid') : themeWarnings(preview, hostTheme()) ? text('themeLowContrast') : '';
  }

  function fill(preset) {
    draftName = preset?.name ?? null;
    for (const field of fields) node(field).value = String(preset?.[field] ?? '');
    for (const mode of modes) node(mode).value = preset?.modeAccent?.[mode] ?? '';
  }

  function draft() {
    const result = {};
    if (draftName != null) result.name = draftName;
    for (const field of fields) {
      const value = node(field).value.trim();
      if (value) result[field] = field === 'fontSize' || field === 'borderRadius' ? Number(value) : value;
    }
    const modeAccent = {};
    for (const mode of modes) {
      const value = node(mode).value.trim();
      if (value) modeAccent[mode] = value;
    }
    if (Object.keys(modeAccent).length) result.modeAccent = modeAccent;
    return result;
  }

  function edit() {
    if (!ready || busy) return;
    const candidate = draft();
    valid = isThemePreset(candidate) && inputs.every(input => input.validity?.valid !== false);
    preview = valid ? candidate : saved;
    status = '';
    display(preview);
    render();
  }

  function discard() {
    importGeneration++;
    preview = saved; valid = true; status = '';
    fill(saved); display(saved); render();
  }

  async function load() {
    busy = true; render();
    try {
      const result = await bridge.request('byok.theme.get', {});
      saved = result.theme; preview = saved; ready = true; status = ''; valid = true;
      fill(saved); display(saved);
    } catch {
      ready = false; status = 'themeLoadFailed';
    } finally { busy = false; render(); }
  }

  async function save(event) {
    event.preventDefault();
    if (!ready || busy || !valid) return;
    const candidate = draft();
    busy = true; render();
    try {
      const result = await bridge.request('byok.theme.set', { theme: candidate });
      saved = result.theme; preview = saved; fill(saved); display(saved); status = 'themeSaved';
    } catch { status = 'themeSaveFailed'; }
    finally { busy = false; render(); }
  }

  async function reset() {
    if (busy || !bridge.isConnected()) return;
    busy = true; render();
    try {
      await bridge.request('byok.theme.reset', {});
      saved = null; preview = null; ready = true; valid = true;
      fill(null); display(null); status = 'themeResetDone';
    } catch { status = 'themeSaveFailed'; }
    finally { busy = false; render(); }
  }

  function selectPreset() {
    if (!ready || busy) return;
    const preset = bundledThemes[node('preset').value];
    node('preset').value = '';
    if (!preset) return;
    fill(preset); edit();
  }

  async function importFile() {
    const file = node('import').files?.[0];
    node('import').value = '';
    if (!ready || busy || !file) return;
    const generation = ++importGeneration;
    busy = true; render();
    try {
      if (file.size > MAX_THEME_FILE_BYTES) throw new Error('file_too_large');
      const candidate = parseThemeFile(await file.text());
      if (generation !== importGeneration) return;
      fill(candidate); preview = candidate; valid = true;
      display(preview); status = 'themeImported';
    } catch {
      if (generation === importGeneration) status = 'themeImportFailed';
    } finally {
      busy = false;
      if (generation !== importGeneration) discard();
      else render();
    }
  }

  function exportFile() {
    if (!ready || busy || !valid) return;
    let url;
    try {
      const file = new Blob([JSON.stringify(draft(), null, 2) + '\n'], { type: 'application/json' });
      url = URL.createObjectURL(file);
      const link = document.createElement('a');
      link.href = url; link.download = 'lumen-theme.json';
      link.click();
    } catch { status = 'themeExportFailed'; render(); }
    finally { if (url) setTimeout(() => URL.revokeObjectURL(url), 1000); }
  }

  form.addEventListener('input', edit);
  form.addEventListener('change', edit);
  form.addEventListener('submit', save);
  node('preset').addEventListener('change', selectPreset);
  node('import').addEventListener('change', importFile);
  node('export').addEventListener('click', exportFile);
  node('cancel').addEventListener('click', discard);
  node('reset').addEventListener('click', reset);
  panel.addEventListener('toggle', () => {
    if (busy) { if (!panel.open) importGeneration++; return; }
    if (!panel.open) discard();
    else if (!ready && bridge.isConnected()) void load();
  });
  render();
  return { load, refresh: render, current: () => preview };
}
