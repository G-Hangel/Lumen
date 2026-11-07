export const PROVIDER_KINDS = Object.freeze(['OpenAI', 'OpenRouter', 'Anthropic', 'Gemini', 'Ollama', 'Azure', 'LMStudio']);
export const EFFORTS = Object.freeze(['Off', 'Minimal', 'Low', 'Medium', 'High', 'Max']);
export const PROBE_CAPABILITIES = Object.freeze(['Vision', 'Tools']);
export const PROBE_PROVIDER_KINDS = Object.freeze(['OpenAI', 'OpenRouter', 'LMStudio', 'Azure']);
export const MAX_PROBE_BUDGET = 5;
export const IMAGE_TYPES = Object.freeze(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
export const MAX_IMAGE_BYTES = 4 * 1024 * 1024;
const MAX_WIRE_SIZE = 8 * 1024 * 1024;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const guid = value => typeof value === 'string' && uuid.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const fields = (value, required, optional = []) => record(value)
  && required.every(key => Object.hasOwn(value, key))
  && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const string = (value, limit, empty = false) => typeof value === 'string' && value.length <= limit && (empty || Boolean(value.trim()));
const list = (value, maximum, predicate) => Array.isArray(value) && value.length <= maximum && value.every(predicate);
const unique = (value, select = item => item) => new Set(value.map(select)).size === value.length;
const codes = new Set(['invalid_message', 'invalid_response', 'bridge_unavailable', 'bridge_busy', 'timeout', 'cancelled',
  'invalid_key', 'permission_denied', 'invalid_url', 'rate_limited', 'insufficient_credit', 'network_error', 'unsafe_url',
  'redirect_rejected', 'provider_error', 'provider_not_found', 'model_not_found', 'vision_not_supported', 'tools_not_supported',
  'invalid_image_type', 'image_too_large', 'payload_too_large', 'stream_too_large', 'unsupported_type']);
export const safeErrorCode = code => codes.has(code) ? code : 'unknown';

export class ByokError extends Error {
  constructor(code) { super(safeErrorCode(code)); this.name = 'ByokError'; this.code = safeErrorCode(code); }
}

export function normalizeBaseUrl(value) {
  if (!string(value, 2048) || /[\s\\]/.test(value)) throw new ByokError('invalid_url');
  let url;
  try { url = new URL(value); } catch { throw new ByokError('invalid_url'); }
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname.toLowerCase());
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && loopback))
    || url.username || url.password || url.search || url.hash || /[?#]/.test(value)) throw new ByokError('invalid_url');
  url.pathname = `${url.pathname.replace(/\/+$/, '')}/`;
  return url.href;
}
const validUrl = value => { try { normalizeBaseUrl(value); return true; } catch { return false; } };
const deployments = value => list(value, 256, name => string(name, 512)) && unique(value);
const configuration = value => fields(value, ['id', 'providerId', 'kind', 'baseUrl', 'selectedModelId', 'deploymentNames'])
  && guid(value.id) && string(value.providerId, 64) && PROVIDER_KINDS.includes(value.kind) && validUrl(value.baseUrl)
  && (value.selectedModelId === null || string(value.selectedModelId, 512)) && deployments(value.deploymentNames);
const origin = value => fields(value, ['capability', 'source', 'confidence']) && string(value.capability, 64)
  && ['Native', 'Registry', 'Heuristic', 'Probe', 'UserOverride'].includes(value.source)
  && ['Low', 'Medium', 'High'].includes(value.confidence);
export const isModel = value => fields(value, ['id', 'name', 'vision', 'tools', 'reasoning', 'origins', 'efforts'])
  && string(value.id, 512) && string(value.name, 512) && ['vision', 'tools', 'reasoning'].every(key => typeof value[key] === 'boolean')
  && list(value.origins, 32, origin) && unique(value.origins, item => item.capability)
  && list(value.efforts, 6, effort => EFFORTS.includes(effort)) && unique(value.efforts);
const errorCode = value => string(value, 64) && /^[a-z][a-z0-9_]*$/.test(value);
const tokenCount = value => Number.isSafeInteger(value) && value >= 0;
const probeBudget = value => Number.isInteger(value) && value >= 0 && value <= MAX_PROBE_BUDGET;

// Bound structure as well as strings: tool arguments are external JSON, never executable instructions.
function boundedJson(value) {
  let size = 0, nodes = 0;
  function visit(item, depth) {
    if (++nodes > 100000 || depth > 16) return false;
    if (item === null || typeof item === 'boolean') size += 5;
    else if (typeof item === 'number') { if (!Number.isFinite(item)) return false; size += 24; }
    else if (typeof item === 'string') size += item.length * 2 + 2;
    else if (Array.isArray(item)) { for (const child of item) if (!visit(child, depth + 1)) return false; }
    else if (record(item)) {
      for (const key of Object.keys(item)) { size += key.length * 2 + 4; if (!visit(item[key], depth + 1)) return false; }
    } else return false;
    return size <= MAX_WIRE_SIZE;
  }
  return visit(value, 0);
}

export function isByokEvent(value) {
  if (!record(value)) return false;
  switch (value.kind) {
    case 'TextDelta': case 'ReasoningDelta': return fields(value, ['kind', 'text']) && string(value.text, 1024 * 1024, true);
    case 'ToolCallStart': return fields(value, ['kind', 'id', 'name']) && string(value.id, 512) && typeof value.name === 'string' && /^[a-zA-Z0-9_-]{1,64}$/.test(value.name);
    case 'ToolCallArgsDelta': return fields(value, ['kind', 'id', 'chunk']) && string(value.id, 512) && string(value.chunk, 1024 * 1024, true);
    case 'ToolCallEnd': return fields(value, ['kind', 'id', 'arguments']) && string(value.id, 512) && record(value.arguments) && boundedJson(value.arguments);
    case 'Usage': return fields(value, ['kind', 'input', 'output']) && tokenCount(value.input) && tokenCount(value.output);
    case 'Done': return fields(value, ['kind', 'reason']) && ['Stop', 'MaxTokens', 'ToolUse', 'ContentFiltered', 'Cancelled', 'Other'].includes(value.reason);
    case 'Error': return fields(value, ['kind', 'code']) && errorCode(value.code);
    default: return false;
  }
}

export function isByokEnvelope(value) {
  if (!fields(value, ['version', 'id', 'type', 'payload']) || value.version !== 1 || !guid(value.id) || !boundedJson(value)) return false;
  const p = value.payload;
  switch (value.type) {
    case 'error': return fields(p, ['code']) && errorCode(p.code);
    case 'byok.list.result': return fields(p, ['configurations', 'remainingBudget']) && probeBudget(p.remainingBudget)
      && list(p.configurations, 256, configuration) && unique(p.configurations, item => item.id);
    case 'byok.configure.result': case 'byok.discover.result':
      return fields(p, ['configuration', 'models', 'credentialValid', 'code']) && configuration(p.configuration)
        && list(p.models, 8192, isModel) && unique(p.models, item => item.id)
        && (p.credentialValid === null || typeof p.credentialValid === 'boolean') && (p.code === null || errorCode(p.code));
    case 'byok.select.result': return fields(p, ['configuration']) && configuration(p.configuration);
    case 'byok.override.result': return fields(p, ['model']) && isModel(p.model);
    case 'byok.probe.result': return fields(p, ['model', 'supported', 'remainingBudget']) && isModel(p.model)
      && (p.supported === null || typeof p.supported === 'boolean') && probeBudget(p.remainingBudget);
    case 'byok.chat.result': return fields(p, ['completed']) && typeof p.completed === 'boolean';
    case 'byok.cancel.result': return fields(p, ['cancelled']) && p.cancelled === true;
    case 'byok.event': return isByokEvent(p);
    default: return false;
  }
}

function validRequest(type, p) {
  switch (type) {
    case 'byok.list': case 'byok.cancel': return fields(p, []);
    case 'byok.configure': return fields(p, ['id', 'providerId', 'kind', 'baseUrl', 'key'], ['deploymentNames'])
      && guid(p.id) && string(p.providerId, 64) && PROVIDER_KINDS.includes(p.kind) && validUrl(p.baseUrl)
      && string(p.key, 4096, true) && !/[\r\n]/.test(p.key) && (!Object.hasOwn(p, 'deploymentNames') || deployments(p.deploymentNames));
    case 'byok.discover': return fields(p, ['configurationId']) && guid(p.configurationId);
    case 'byok.probe': return fields(p, ['configurationId', 'capability', 'consent']) && guid(p.configurationId)
      && PROBE_CAPABILITIES.includes(p.capability) && p.consent === true;
    case 'byok.select': return fields(p, ['configurationId', 'modelId']) && guid(p.configurationId) && string(p.modelId, 512);
    case 'byok.override': return fields(p, ['configurationId', 'modelId', 'vision', 'tools', 'reasoning'])
      && guid(p.configurationId) && string(p.modelId, 512) && ['vision', 'tools', 'reasoning'].every(key => typeof p[key] === 'boolean');
    case 'byok.chat': return fields(p, ['configurationId', 'text', 'effort'], ['image']) && guid(p.configurationId)
      && string(p.text, 4096) && EFFORTS.includes(p.effort) && (!Object.hasOwn(p, 'image') || (fields(p.image, ['mediaType', 'base64Data'])
        && IMAGE_TYPES.includes(p.image.mediaType) && string(p.image.base64Data, Math.ceil(MAX_IMAGE_BYTES / 3) * 4)
        && p.image.base64Data.length % 4 === 0 && /^[A-Za-z0-9+/]+={0,2}$/.test(p.image.base64Data)
        && p.image.base64Data.length / 4 * 3 - (p.image.base64Data.endsWith('==') ? 2 : p.image.base64Data.endsWith('=') ? 1 : 0) <= MAX_IMAGE_BYTES));
    default: return false;
  }
}

export class ByokBridge {
  constructor(transport, { connected = () => false, timeoutMs = 120000, newId = () => crypto.randomUUID() } = {}) {
    this.transport = transport;
    this.available = Boolean(transport && ['postMessage', 'addEventListener', 'removeEventListener'].every(key => typeof transport[key] === 'function'));
    this.isConnected = connected;
    this.timeoutMs = timeoutMs;
    this.newId = newId;
    this.pending = new Map();
    this.usedIds = new Set();
    this.listener = event => this.receive(event.data);
    if (this.available) transport.addEventListener('message', this.listener);
  }

  request(type, payload = {}, { onEvent = () => {}, expectedModelId } = {}) {
    if (!this.available || !this.isConnected()) return Promise.reject(new ByokError('bridge_unavailable'));
    if (!['byok.list', 'byok.configure', 'byok.discover', 'byok.select', 'byok.override', 'byok.probe', 'byok.chat', 'byok.cancel'].includes(type)) return Promise.reject(new ByokError('unsupported_type'));
    if (!validRequest(type, payload) || (type === 'byok.probe' && expectedModelId !== undefined && !string(expectedModelId, 512))) return Promise.reject(new ByokError('invalid_message'));
    if (this.pending.size && (type !== 'byok.cancel' || [...this.pending.values()].some(value => value.type === 'byok.cancel'))) return Promise.reject(new ByokError('bridge_busy'));
    if (this.usedIds.size >= 16384) return Promise.reject(new ByokError('bridge_busy'));
    const id = this.newId();
    if (!guid(id) || this.usedIds.has(id)) return Promise.reject(new ByokError('invalid_message'));
    // Do not evict IDs during this bridge's lifetime: delayed completions cannot become new requests.
    this.usedIds.add(id);
    const expected = type === 'byok.configure' ? { configurationId: payload.id, providerId: payload.providerId, kind: payload.kind }
      : { configurationId: payload.configurationId, modelId: type === 'byok.probe' ? expectedModelId : payload.modelId };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.finish(id, new ByokError('timeout')), this.timeoutMs);
      this.pending.set(id, { type, expected, onEvent, resolve, reject, timer, terminal: null, tools: new Map(), streamSize: 0, eventCount: 0 });
      try { this.transport.postMessage({ version: 1, id, type, payload }); }
      catch { this.finish(id, new ByokError('bridge_unavailable')); }
      finally { payload = null; }
    });
  }

  finish(id, error, payload) {
    const pending = this.pending.get(id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(id);
    if (error) pending.reject(error); else pending.resolve(payload);
  }

  receive(message) {
    // The spike bridge owns ready/ping/theme and its own errors. Never consume another listener's correlation.
    if (!record(message) || !this.pending.has(message.id)) return false;
    const pending = this.pending.get(message.id);
    if (!isByokEnvelope(message)) { this.finish(message.id, new ByokError('invalid_response')); return false; }
    if (message.type === 'error') { this.finish(message.id, new ByokError(message.payload.code)); return true; }
    const p = message.payload;
    if (message.type === 'byok.event') {
      if (pending.type !== 'byok.chat' || pending.terminal) { this.finish(message.id, new ByokError('invalid_response')); return false; }
      if (++pending.eventCount > 32768) { this.finish(message.id, new ByokError('stream_too_large')); return false; }
      if (p.kind === 'ToolCallStart') {
        if (pending.tools.has(p.id) || pending.tools.size >= 128) { this.finish(message.id, new ByokError('invalid_response')); return false; }
        pending.tools.set(p.id, 'started');
      } else if (p.kind === 'ToolCallArgsDelta' || p.kind === 'ToolCallEnd') {
        if (pending.tools.get(p.id) !== 'started') { this.finish(message.id, new ByokError('invalid_response')); return false; }
        if (p.kind === 'ToolCallEnd') pending.tools.set(p.id, 'ended');
      }
      pending.streamSize += (p.text?.length || p.chunk?.length || 0) + (p.arguments ? JSON.stringify(p.arguments).length : 0);
      if (pending.streamSize > MAX_WIRE_SIZE) { this.finish(message.id, new ByokError('stream_too_large')); return false; }
      if (p.kind === 'Done' || p.kind === 'Error') pending.terminal = p.kind;
      try { pending.onEvent(p.kind === 'Error' ? { kind: 'Error', code: safeErrorCode(p.code) } : p); }
      catch { this.finish(message.id, new ByokError('invalid_response')); return false; }
      return true;
    }
    const expected = pending.expected;
    if (message.type !== `${pending.type}.result`
      || (p.configuration && expected.configurationId && p.configuration.id !== expected.configurationId)
      || (pending.type === 'byok.configure' && (p.configuration.providerId !== expected.providerId || p.configuration.kind !== expected.kind))
      || (pending.type === 'byok.select' && p.configuration.selectedModelId !== expected.modelId)
      || (pending.type === 'byok.override' && p.model.id !== expected.modelId)
      || (pending.type === 'byok.probe' && expected.modelId !== undefined && p.model.id !== expected.modelId)
      || (pending.type === 'byok.chat' && pending.terminal === 'Error' && p.completed)) {
      this.finish(message.id, new ByokError('invalid_response')); return false;
    }
    this.finish(message.id, null, p);
    return true;
  }

  disconnect() { for (const id of this.pending.keys()) this.finish(id, new ByokError('bridge_unavailable')); }
  dispose() {
    if (this.available) this.transport.removeEventListener('message', this.listener);
    this.disconnect(); this.available = false; this.transport = null; this.usedIds.clear();
  }
}
