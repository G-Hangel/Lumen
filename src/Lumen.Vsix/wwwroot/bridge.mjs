export const MAX_TEXT_LENGTH = 4096;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const color = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const keysAre = (value, required, optional = []) => record(value)
  && required.every(key => Object.hasOwn(value, key))
  && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const validCulture = value => value === 'pt-BR' || value === 'en';

export function isTheme(value, requireCulture = false) {
  return keysAre(value, ['background', 'foreground', 'accent', ...(requireCulture ? ['culture'] : [])], requireCulture ? [] : ['culture'])
    && ['background', 'foreground', 'accent'].every(key => typeof value[key] === 'string' && color.test(value[key]))
    && (!Object.hasOwn(value, 'culture') || validCulture(value.culture));
}

export function isEnvelope(value) {
  if (!keysAre(value, ['version', 'id', 'type', 'payload']) || value.version !== 1) return false;
  if (value.type === 'error') {
    return (value.id === null || (typeof value.id === 'string' && uuid.test(value.id)))
      && keysAre(value.payload, ['code']) && typeof value.payload.code === 'string'
      && /^[a-z][a-z0-9_]{0,63}$/.test(value.payload.code);
  }
  if (typeof value.id !== 'string' || !uuid.test(value.id)) return false;
  if (value.type === 'ready.result') return isTheme(value.payload, true);
  if (value.type === 'theme.changed') return isTheme(value.payload);
  return value.type === 'ping.result' && keysAre(value.payload, ['text'])
    && typeof value.payload.text === 'string' && value.payload.text.length <= MAX_TEXT_LENGTH;
}

export class BridgeError extends Error {
  constructor(code) {
    super(code);
    this.code = code;
  }
}

export class HostBridge {
  constructor(transport, { onTheme = () => {}, onError = () => {}, timeoutMs = 8000, newId = () => crypto.randomUUID() } = {}) {
    this.transport = transport;
    this.available = Boolean(transport && typeof transport.postMessage === 'function'
      && typeof transport.addEventListener === 'function' && typeof transport.removeEventListener === 'function');
    this.connected = false;
    this.pending = new Map();
    this.recentIds = new Set();
    this.onTheme = onTheme;
    this.onError = onError;
    this.timeoutMs = timeoutMs;
    this.newId = newId;
    this.listener = event => this.receive(event.data);
    if (this.available) transport.addEventListener('message', this.listener);
  }

  request(type, payload = {}) {
    if (!this.available) return Promise.reject(new BridgeError('bridge_unavailable'));
    if ((type !== 'ready' && type !== 'ping')
      || (type === 'ready' && !keysAre(payload, []))
      || (type === 'ping' && (!keysAre(payload, ['text']) || typeof payload.text !== 'string'
        || !payload.text.trim() || payload.text.length > MAX_TEXT_LENGTH))) {
      return Promise.reject(new BridgeError('invalid_message'));
    }
    if (type === 'ping' && !this.connected) return Promise.reject(new BridgeError('bridge_unavailable'));
    if (this.pending.size) return Promise.reject(new BridgeError('bridge_busy'));
    const id = this.newId();
    this.rememberId(id);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new BridgeError('timeout'));
      }, this.timeoutMs);
      this.pending.set(id, { type, payload, resolve, reject, timer });
      try {
        this.transport.postMessage({ version: 1, id, type, payload });
      } catch {
        this.finish(id, new BridgeError('bridge_unavailable'));
      }
    });
  }

  rememberId(id) {
    this.recentIds.add(id);
    if (this.recentIds.size > 256) this.recentIds.delete(this.recentIds.values().next().value);
  }

  finish(id, error, payload) {
    const pending = this.pending.get(id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(id);
    if (error) pending.reject(error);
    else pending.resolve(payload);
  }

  receive(message) {
    if (record(message) && ((typeof message.type === 'string' && message.type.startsWith('byok.')) || (message.type === 'error' && message.id !== null && !this.pending.has(message.id)))) return;
    if (!isEnvelope(message)) {
      if (record(message) && this.pending.has(message.id)) this.finish(message.id, new BridgeError('invalid_response'));
      else this.onError('invalid_response');
      return;
    }
    if (message.type === 'theme.changed') {
      if (!this.connected || this.pending.has(message.id) || this.recentIds.has(message.id)) {
        this.onError('invalid_response');
        return;
      }
      this.rememberId(message.id);
      this.onTheme(message.payload);
      return;
    }
    if (message.type === 'error' && message.id === null) {
      this.onError(message.payload.code);
      return;
    }
    const pending = this.pending.get(message.id);
    if (!pending) {
      this.onError('invalid_response');
      return;
    }
    if (message.type === 'error') {
      this.finish(message.id, new BridgeError(message.payload.code));
    } else if (message.type !== `${pending.type}.result`
      || (pending.type === 'ping' && message.payload.text !== pending.payload.text)) {
      this.finish(message.id, new BridgeError('invalid_response'));
    } else {
      if (pending.type === 'ready') this.connected = true;
      this.finish(message.id, null, message.payload);
    }
  }

  dispose() {
    if (this.available) this.transport.removeEventListener('message', this.listener);
    for (const id of this.pending.keys()) this.finish(id, new BridgeError('bridge_unavailable'));
    this.connected = false;
    this.available = false;
  }
}
