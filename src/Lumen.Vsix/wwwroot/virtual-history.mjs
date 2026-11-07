export const ROW_HEIGHT = 112;
export const OVERSCAN = 3;

export function visibleRange(count, scrollTop, height) {
  const totalHeight = count * ROW_HEIGHT;
  const top = Math.max(0, Math.min(scrollTop, Math.max(0, totalHeight - height)));
  const first = Math.floor(top / ROW_HEIGHT);
  const start = Math.max(0, first - OVERSCAN);
  const end = Math.min(count, Math.ceil((top + height) / ROW_HEIGHT) + OVERSCAN);
  return { start, end, offset: start * ROW_HEIGHT, totalHeight };
}

export class VirtualHistory {
  constructor(viewport, spacer, layer, renderMessage, onRange) {
    this.viewport = viewport;
    this.spacer = spacer;
    this.layer = layer;
    this.renderMessage = renderMessage;
    this.onRange = onRange;
    this.items = [];
    this.signature = '';
    this.frame = 0;
    this.schedule = () => {
      if (!this.frame) this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.render();
      });
    };
    viewport.addEventListener('scroll', this.schedule, { passive: true });
    this.resize = new ResizeObserver(this.schedule);
    this.resize.observe(viewport);
  }

  setItems(items) {
    this.items = items;
    this.signature = '';
    this.spacer.style.height = `${items.length * ROW_HEIGHT}px`;
    this.render();
  }

  render() {
    const range = visibleRange(this.items.length, this.viewport.scrollTop, this.viewport.clientHeight);
    const signature = `${range.start}:${range.end}:${this.items.length}`;
    if (signature === this.signature) return;
    this.signature = signature;
    const fragment = document.createDocumentFragment();
    for (let index = range.start; index < range.end; index++) {
      const row = this.renderMessage(this.items[index], index);
      row.style.height = `${ROW_HEIGHT}px`;
      row.setAttribute('role', 'listitem');
      row.setAttribute('aria-posinset', String(index + 1));
      row.setAttribute('aria-setsize', String(this.items.length));
      fragment.append(row);
    }
    this.layer.style.transform = `translateY(${range.offset}px)`;
    this.layer.replaceChildren(fragment);
    this.onRange(range, this.items.length);
  }

  jump(end) {
    this.viewport.scrollTop = end ? this.items.length * ROW_HEIGHT : 0;
    this.render();
    this.viewport.focus({ preventScroll: true });
  }

  dispose() {
    this.resize.disconnect();
    this.viewport.removeEventListener('scroll', this.schedule);
    cancelAnimationFrame(this.frame);
  }
}
