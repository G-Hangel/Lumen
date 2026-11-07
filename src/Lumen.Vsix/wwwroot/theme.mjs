function luminance(hex) {
  const full = hex.length === 4 ? hex.slice(1).split('').map(value => value + value).join('') : hex.slice(1);
  const channels = [0, 2, 4].map(offset => {
    const value = parseInt(full.slice(offset, offset + 2), 16) / 255;
    return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
  });
  return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
}

export function contrast(a, b) {
  const first = luminance(a);
  const second = luminance(b);
  return (Math.max(first, second) + .05) / (Math.min(first, second) + .05);
}

export function themeProperties({ background, foreground, accent }) {
  const dark = luminance(background) < .35;
  const readable = candidate => contrast(candidate, background) >= 4.5 ? candidate : foreground;
  return {
    '--vs-background': background,
    '--vs-foreground': foreground,
    '--vs-accent': accent,
    '--link-color': readable(accent),
    '--focus-color': contrast(accent, background) >= 3 ? accent : foreground,
    '--button-foreground': contrast('#000000', accent) > contrast('#ffffff', accent) ? '#000000' : '#ffffff',
    '--syntax-string': readable(dark ? '#9bd6ad' : '#216e3e'),
    'color-scheme': dark ? 'dark' : 'light',
  };
}
