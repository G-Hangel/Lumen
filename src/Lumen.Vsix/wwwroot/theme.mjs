function channels(hex) {
  const full = hex.length === 4 ? hex.slice(1).split('').map(value => value + value).join('') : hex.slice(1);
  return [0, 2, 4].map(offset => parseInt(full.slice(offset, offset + 2), 16));
}

function toHex(values) {
  return '#' + values.map(value => Math.round(Math.max(0, Math.min(255, value))).toString(16).padStart(2, '0')).join('');
}

function luminance(hex) {
  const [red, green, blue] = channels(hex).map(value => {
    const channel = value / 255;
    return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
  });
  return red * .2126 + green * .7152 + blue * .0722;
}

export function contrast(a, b) {
  const first = luminance(a);
  const second = luminance(b);
  return (Math.max(first, second) + .05) / (Math.min(first, second) + .05);
}

export function mix(a, b, weight) {
  const first = channels(a), second = channels(b);
  return toHex(first.map((value, index) => value + (second[index] - value) * weight));
}

export function ensureContrast(color, background, ratio) {
  if (contrast(color, background) >= ratio) return color;
  const target = luminance(background) < .35 ? '#ffffff' : '#000000';
  for (let step = 1; step <= 40; step++) {
    const candidate = mix(color, target, step / 40);
    if (contrast(candidate, background) >= ratio) return candidate;
  }
  return target;
}

const emerald = '#00875a', teal = '#008f80', mint = '#00806b';
const success = '#3fb950', warning = '#d29922', danger = '#f85149', information = '#58a6ff';

export const MODE_ACCENTS = ['ask', 'plan', 'agent'];

let currentPreset = null;
let currentBackground = '#131318';
let currentMode = null;

function targetRoot(root) {
  return root ?? globalThis.document?.documentElement ?? null;
}

function setOrRemove(root, property, value) {
  if (!root) return;
  if (value) root.style.setProperty(property, value);
  else root.style.removeProperty(property);
}

// A custom preset only overrides the colors it fully defines; everything else keeps the VS theme.
export function presetColors(preset, vsTheme) {
  return preset?.background && preset?.foreground && preset?.accent
    ? { background: preset.background, foreground: preset.foreground, accent: preset.accent }
    : vsTheme;
}

export function applyPreset(preset, vsTheme, root) {
  currentPreset = preset ?? null;
  const colors = presetColors(currentPreset, vsTheme);
  currentBackground = colors.background;
  const element = targetRoot(root);
  if (!element) return colors;
  element.dataset.themeSource = currentPreset ? 'custom' : 'vs';
  for (const [property, value] of Object.entries(themeProperties(colors))) element.style.setProperty(property, value);
  setOrRemove(element, '--chat-font', currentPreset?.chatFont);
  setOrRemove(element, '--mono', currentPreset?.codeFont);
  setOrRemove(element, '--font-size', currentPreset?.fontSize ? `${currentPreset.fontSize}px` : null);
  setOrRemove(element, '--radius', currentPreset?.borderRadius != null ? `${currentPreset.borderRadius}px` : null);
  if (currentPreset?.density && currentPreset.density !== 'normal') element.setAttribute('data-density', currentPreset.density);
  else element.removeAttribute('data-density');
  for (const mode of MODE_ACCENTS) {
    const color = currentPreset?.modeAccent?.[mode];
    setOrRemove(element, `--accent-${mode}`, color ? ensureContrast(color, currentBackground, 3) : null);
  }
  applyModeAccent(element);
  return colors;
}

// The accent of the mode currently selected in the composer.
export function applyModeAccent(root) {
  const element = targetRoot(root);
  if (!element) return;
  const color = currentPreset?.modeAccent?.[currentMode];
  setOrRemove(element, '--mode-accent', color ? ensureContrast(color, currentBackground, 3) : null);
}

export function setMode(mode) {
  currentMode = typeof mode === 'string' ? mode.toLowerCase() : null;
  applyModeAccent();
}

export function themeProperties({ background, foreground, accent }) {
  const dark = luminance(background) < .35;
  const readable = candidate => contrast(candidate, background) >= 4.5 ? candidate : foreground;
  const ink = candidate => ensureContrast(candidate, background, 4.5);
  const mark = candidate => ensureContrast(candidate, background, 3);
  const brand = mark(emerald);
  const wash = (candidate, weight) => mix(candidate, background, weight);
  // Text sits on the tinted wash at least as often as on the raw background, so it must clear both.
  const tinted = hue => {
    const base = ink(hue), surface = wash(base, dark ? .86 : .9);
    return { ink: ensureContrast(base, surface, 4.5), wash: surface };
  };
  const brandWash = wash(brand, dark ? .86 : .9);
  const okTint = tinted(success), warnTint = tinted(warning), dangerTint = tinted(danger), infoTint = tinted(information);
  const brandDeep = mix(emerald, '#000000', .18), mintDeep = mix(mint, '#000000', .18);
  return {
    '--vs-background': background,
    '--vs-foreground': foreground,
    '--vs-accent': accent,
    '--link-color': readable(accent),
    '--focus-color': contrast(accent, background) >= 3 ? accent : foreground,
    '--button-foreground': contrast('#000000', accent) > contrast('#ffffff', accent) ? '#000000' : '#ffffff',
    '--syntax-string': readable(dark ? '#9bd6ad' : '#216e3e'),
    '--brand': brand,
    '--brand-ink': ensureContrast(ink(emerald), brandWash, 4.5),
    '--brand-deep': brandDeep,
    '--orchid-deep': mintDeep,
    '--on-brand': Math.min(contrast('#ffffff', brandDeep), contrast('#ffffff', mintDeep)) >= 4.5 ? '#ffffff' : '#000000',
    '--brand-wash': brandWash,
    '--brand-edge': wash(brand, .58),
    '--indigo': mark(teal),
    '--orchid': mark(mint),
    '--ok': okTint.ink,
    '--warn': warnTint.ink,
    '--danger': dangerTint.ink,
    '--info': infoTint.ink,
    '--ok-wash': okTint.wash,
    '--warn-wash': warnTint.wash,
    '--danger-wash': dangerTint.wash,
    '--info-wash': infoTint.wash,
    '--ink-dim': ink(mix(foreground, background, .3)),
    '--ink-faint': ink(mix(foreground, background, .5)),
    '--surface-1': mix(foreground, background, .965),
    '--surface-2': mix(foreground, background, .93),
    '--surface-3': mix(foreground, background, .87),
    '--scrim': dark ? '#05050a99' : '#f4f4f899',
    'color-scheme': dark ? 'dark' : 'light',
  };
}
