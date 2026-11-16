import { isThemePreset } from './byok-bridge.mjs';
import { contrast } from './theme.mjs';

const fields = ['background', 'foreground', 'accent', 'chatFont', 'codeFont', 'fontSize', 'density', 'borderRadius'];
const modes = ['ask', 'plan', 'agent'];

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
  const inputs = [...fields, ...modes].map(node);

  function render() {
    node('apply').disabled = !ready || busy || !valid;
    node('reset').disabled = busy || !bridge.isConnected();
    node('cancel').disabled = busy;
    for (const input of inputs) input.disabled = busy || !ready;
    node('status').textContent = status ? text(status) : '';
    node('warning').textContent = !valid ? text('themeInvalid') : themeWarnings(preview, hostTheme()) ? text('themeLowContrast') : '';
  }

  function fill(preset) {
    for (const field of fields) node(field).value = String(preset?.[field] ?? '');
    for (const mode of modes) node(mode).value = preset?.modeAccent?.[mode] ?? '';
  }

  function draft() {
    const result = {};
    if (saved?.name != null) result.name = saved.name;
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

  form.addEventListener('input', edit);
  form.addEventListener('change', edit);
  form.addEventListener('submit', save);
  node('cancel').addEventListener('click', discard);
  node('reset').addEventListener('click', reset);
  panel.addEventListener('toggle', () => {
    if (busy) return;
    if (!panel.open) discard();
    else if (!ready && bridge.isConnected()) void load();
  });
  render();
  return { load, refresh: render, current: () => preview };
}
