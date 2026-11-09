export const ROW_HEIGHT = 112;
export const OVERSCAN = 3;

export function visibleRange(count, scrollTop, height, heights = []) {
  const offsets = [0];
  for (let index = 0; index < count; index++) offsets.push(offsets[index] + (heights[index] || ROW_HEIGHT));
  const totalHeight = offsets[count];
  const top = Math.max(0, Math.min(scrollTop, Math.max(0, totalHeight - height)));
  let first = 0;
  while (first < count && offsets[first + 1] <= top) first++;
  let last = first;
  while (last < count && offsets[last] < top + height) last++;
  const start = Math.max(0, first - OVERSCAN);
  const end = Math.min(count, last + OVERSCAN);
  return { start, end, offset: offsets[start], totalHeight };
}

export class VirtualHistory {
  constructor(viewport, spacer, layer, renderMessage, onRange) {
    this.viewport = viewport; this.spacer = spacer; this.layer = layer;
    this.renderMessage = renderMessage; this.onRange = onRange;
    this.items = []; this.heights = []; this.signature = ''; this.frame = 0; this.mounted = new Map();
    this.schedule = () => {
      if (!this.frame) this.frame = requestAnimationFrame(() => { this.frame = 0; this.render(); });
    };
    viewport.addEventListener('scroll', this.schedule, { passive: true });
    this.resize = new ResizeObserver(() => { this.measure(); this.schedule(); });
    this.resize.observe(viewport);
  }

  setItems(items) {
    this.items = items; this.heights = []; this.mounted.clear(); this.invalidate();
  }

  invalidate() { this.signature = ''; this.render(); }

  measure() {
    let changed = false;
    for (const row of this.layer.children) {
      if (!row.getBoundingClientRect) continue;
      const index = Number(row.getAttribute('aria-posinset')) - 1;
      const height = Math.ceil(row.getBoundingClientRect().height);
      if (height > 0 && this.heights[index] !== height) { this.heights[index] = height; changed = true; }
    }
    return changed;
  }

  render() {
    const range = visibleRange(this.items.length, this.viewport.scrollTop, this.viewport.clientHeight, this.heights);
    this.spacer.style.height = `${range.totalHeight}px`;
    this.layer.style.transform = `translateY(${range.offset}px)`;
    const signature = `${range.start}:${range.end}:${this.items.length}`;
    if (signature === this.signature) return;
    this.signature = signature;
    this.resize.disconnect(); this.resize.observe(this.viewport);
    const fragment = document.createDocumentFragment(), mounted = new Map();
    for (let index = range.start; index < range.end; index++) {
      const previous = this.mounted.get(index);
      const row = previous && previous.item === this.items[index] ? previous.row : this.renderMessage(this.items[index], index);
      mounted.set(index, { item: this.items[index], row });
      row.setAttribute('role', 'listitem');
      row.setAttribute('aria-posinset', String(index + 1));
      row.setAttribute('aria-setsize', String(this.items.length));
      fragment.append(row);
      this.resize.observe(row);
    }
    this.mounted = mounted;
    this.layer.replaceChildren(fragment);
    if (this.measure()) this.schedule();
    this.onRange(range, this.items.length);
  }

  jump(end) {
    this.viewport.scrollTop = end ? visibleRange(this.items.length, 0, 0, this.heights).totalHeight : 0;
    this.render(); this.viewport.focus({ preventScroll: true });
  }

  dispose() {
    this.resize.disconnect(); this.viewport.removeEventListener('scroll', this.schedule);
    cancelAnimationFrame(this.frame);
  }
}
