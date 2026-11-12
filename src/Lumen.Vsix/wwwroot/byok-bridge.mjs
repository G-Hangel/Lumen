export const PROVIDER_KINDS = Object.freeze(['OpenAI', 'OpenRouter', 'Anthropic', 'Gemini', 'Ollama', 'Azure', 'LMStudio']);
export const EFFORTS = Object.freeze(['Off', 'Minimal', 'Low', 'Medium', 'High', 'Max']);
export const PROBE_CAPABILITIES = Object.freeze(['Vision', 'Tools']);
export const PROBE_PROVIDER_KINDS = Object.freeze(['OpenAI', 'OpenRouter', 'LMStudio', 'Azure']);
export const MAX_PROBE_BUDGET = 5;
export const IMAGE_TYPES = Object.freeze(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
export const MAX_IMAGE_BYTES = 4 * 1024 * 1024;
export const MAX_CONTEXTS = 8;
export const MAX_CONTEXT_TEXT = 65536;
export const MAX_CONTEXT_TOTAL = 131072;
export const READ_TOOL_NAMES = Object.freeze(['read_file', 'list_dir', 'find_files', 'grep', 'get_edit_snapshot', 'get_errors', 'get_output_logs', 'detect_shells']);
export const MAX_READ_TOOL_CONTENT = 65536;
export const WRITE_TOOL_NAMES = Object.freeze(['create_file', 'edit_file']);
export const COMMAND_TOOL_NAMES = Object.freeze(['run_command', 'run_build', 'run_tests']);
export const APPROVAL_CATEGORIES = Object.freeze(['WriteOutsideWorkspace', 'ExecuteCommand']);
export const APPROVAL_DECISIONS = Object.freeze(['deny', 'allow_once', 'allow_session']);
export const MAX_WRITE_TOOL_CONTENT = 65536;
export const MAX_WRITE_TOOL_PATH = 1024;
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
const contextKinds = ['file', 'solution', 'selection', 'output'];
const contextIds = value => list(value, MAX_CONTEXTS, guid) && unique(value, id => id.toLowerCase());
const context = value => fields(value, ['id', 'kind', 'label', 'text']) && guid(value.id)
  && contextKinds.includes(value.kind) && typeof value.label === 'string' && value.label.length > 0 && value.label.length <= 1024
  && !/[\u0000-\u001f\u007f-\u009f]/.test(value.label)
  && typeof value.text === 'string' && value.text.length > 0 && value.text.length <= MAX_CONTEXT_TEXT;
const codes = new Set(['invalid_message', 'invalid_response', 'bridge_unavailable', 'bridge_busy', 'timeout', 'cancelled',
  'invalid_key', 'permission_denied', 'invalid_url', 'rate_limited', 'insufficient_credit', 'network_error', 'unsafe_url',
  'redirect_rejected', 'provider_error', 'provider_not_found', 'model_not_found', 'vision_not_supported', 'tools_not_supported',
  'invalid_image_type', 'image_too_large', 'payload_too_large', 'stream_too_large', 'unsupported_type',
  'history_corrupt', 'history_too_large', 'history_write_failed', 'conversation_not_found',
  'context_unavailable', 'context_outside_workspace', 'context_too_large', 'context_stale',
  'unknown_tool', 'invalid_arguments', 'invalid_path', 'path_unavailable', 'outside_workspace',
  'output_too_large', 'file_too_large', 'binary_file', 'workspace_unavailable', 'tool_budget_exceeded',
  'file_exists', 'undo_failed', 'undo_conflict', 'edit_conflict', 'journal_limit', 'mode_denied',
  'approval_denied', 'command_blocked', 'command_timeout', 'command_failed']);
const execution = value => ['Running', 'Completed', 'Failed', 'Cancelled'].includes(value.status)
  && typeof value.isError === 'boolean' && string(value.content, MAX_READ_TOOL_CONTENT, true)
  && (!['Running', 'Cancelled'].includes(value.status) || value.content === '');
const historyTool = call => fields(call, ['id', 'name', 'arguments'], ['status', 'isError', 'content'])
  && string(call.id, 512) && typeof call.name === 'string' && /^[a-zA-Z0-9_-]{1,64}$/.test(call.name)
  && record(call.arguments) && boundedJson(call.arguments)
  && (['status', 'isError', 'content'].every(key => !Object.hasOwn(call, key))
    || (['status', 'isError', 'content'].every(key => Object.hasOwn(call, key)) && execution(call) && call.status !== 'Running'));
export const safeErrorCode = code => codes.has(code) ? code : 'unknown';

export class ByokError extends Error {
  constructor(code, retryAfter = null) {
    super(safeErrorCode(code));
    this.name = 'ByokError';
    this.code = safeErrorCode(code);
    this.retryAfter = Number.isSafeInteger(retryAfter) && retryAfter >= 0 ? retryAfter : null;
  }
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
    case 'ToolExecution': return fields(value, ['kind', 'id', 'status', 'isError', 'content']) && string(value.id, 512) && execution(value);
    case 'ApprovalRequest': return fields(value, ['kind', 'id', 'toolName', 'category', 'arguments', 'targetPath', 'canAllowSession'])
      && guid(value.id) && typeof value.toolName === 'string' && /^[a-zA-Z0-9_-]{1,64}$/.test(value.toolName)
      && APPROVAL_CATEGORIES.includes(value.category) && record(value.arguments) && boundedJson(value.arguments)
      && (value.targetPath === null || string(value.targetPath, 4096, true))
      && value.canAllowSession === (value.category === 'ExecuteCommand');
    case 'Usage': return fields(value, ['kind', 'input', 'output']) && tokenCount(value.input) && tokenCount(value.output);
    case 'Done': return fields(value, ['kind', 'reason']) && ['Stop', 'MaxTokens', 'ToolUse', 'ContentFiltered', 'Cancelled', 'Other'].includes(value.reason);
    case 'Error': return fields(value, ['kind', 'code'], ['retryAfter']) && errorCode(value.code)
      && (!Object.hasOwn(value, 'retryAfter') || value.retryAfter === null || (Number.isSafeInteger(value.retryAfter) && value.retryAfter >= 0));
    default: return false;
  }
}

const summary = value => fields(value, ['id', 'title', 'configurationId', 'modelId', 'updatedAtUtc', 'turnCount'])
  && guid(value.id) && guid(value.configurationId) && string(value.title, 160, true) && string(value.modelId, 512)
  && string(value.updatedAtUtc, 64) && Number.isFinite(Date.parse(value.updatedAtUtc))
  && Number.isInteger(value.turnCount) && value.turnCount >= 0 && value.turnCount <= 10000;
const historyTurn = value => fields(value, ['index', 'userText', 'hasImage', 'text', 'reasoning', 'tools', 'status'], ['contextText'])
  && (!Object.hasOwn(value, 'contextText') || string(value.contextText, 140000, true))
  && Number.isInteger(value.index) && value.index >= 0 && value.index < 10000
  && string(value.userText, 4096, true) && typeof value.hasImage === 'boolean'
  && string(value.text, MAX_WIRE_SIZE, true) && string(value.reasoning, MAX_WIRE_SIZE, true)
  && list(value.tools, 128, historyTool)
  && unique(value.tools, call => call.id) && ['completed', 'cancelled', 'failed'].includes(value.status);

const toolText = (value, limit) => typeof value === 'string' && value.length >= 1 && value.length <= limit
  && !value.includes('\0') && value.isWellFormed();
const toolInteger = (value, max) => Number.isInteger(value) && value >= 1 && value <= max;
const toolGlob = value => toolText(value, 256) && !/^[/\\]|[:\u0000-\u001f\u007f-\u009f"<>|]/.test(value)
  && value.replaceAll('\\', '/').split('/').every(part => part && part !== '.' && part !== '..' && !/[. ]$/.test(part));
export function isReadToolRequest(value) {
  if (!fields(value, ['name', 'arguments']) || !READ_TOOL_NAMES.includes(value.name)) return false;
  const a = value.arguments;
  if (['get_errors', 'get_output_logs', 'detect_shells'].includes(value.name)) return fields(a, []);
  const required = ['path', ...(value.name === 'find_files' ? ['glob'] : value.name === 'grep' ? ['pattern'] : [])];
  const optional = value.name === 'get_edit_snapshot' ? [] : value.name === 'read_file' ? ['startLine', 'endLine'] : ['maxResults', ...(value.name === 'grep' ? ['glob'] : [])];
  return fields(a, required, optional) && toolText(a.path, value.name === 'get_edit_snapshot' ? 1024 : 4096)
    && (!Object.hasOwn(a, 'glob') || toolGlob(a.glob))
    && (!Object.hasOwn(a, 'pattern') || toolText(a.pattern, 1024))
    && (!Object.hasOwn(a, 'maxResults') || toolInteger(a.maxResults, 256))
    && (!Object.hasOwn(a, 'startLine') || toolInteger(a.startLine, 1000000))
    && (!Object.hasOwn(a, 'endLine') || (toolInteger(a.endLine, 1000000) && a.endLine >= (a.startLine ?? 1)));
}
function readToolResult(value) {
  if (!fields(value, ['name', 'isError', 'content']) || !READ_TOOL_NAMES.includes(value.name)
    || typeof value.isError !== 'boolean' || typeof value.content !== 'string' || value.content.length > MAX_READ_TOOL_CONTENT) return false;
  try {
    const content = JSON.parse(value.content);
    return record(content) && boundedJson(content) && (!value.isError || (fields(content, ['code']) && errorCode(content.code)));
  } catch { return false; }
}

const pendingWrites = value => Number.isInteger(value) && value >= 0 && value <= 64;
export function isWriteToolRequest(value) {
  if (!fields(value, ['name', 'arguments']) || !WRITE_TOOL_NAMES.includes(value.name)) return false;
  const a = value.arguments;
  // This is input hygiene only. The native host owns canonicalization and solution confinement.
  return fields(a, ['path', 'content']) && toolText(a.path, MAX_WRITE_TOOL_PATH)
    && !/^[/\\]|[:\u0000-\u001f\u007f-\u009f"<>|?*]/.test(a.path)
    && a.path.replaceAll('\\', '/').split('/').every(part => part && part !== '.' && part !== '..' && !/[. ]$/.test(part))
    && typeof a.content === 'string' && a.content.length <= MAX_WRITE_TOOL_CONTENT
    && !a.content.includes('\0') && a.content.isWellFormed();
}
function writeToolResult(value) {
  if (!fields(value, ['name', 'isError', 'content', 'pending']) || !WRITE_TOOL_NAMES.includes(value.name)
    || typeof value.isError !== 'boolean' || typeof value.content !== 'string'
    || value.content.length > MAX_WRITE_TOOL_CONTENT || !pendingWrites(value.pending)) return false;
  try {
    const content = JSON.parse(value.content);
    if (!record(content) || !boundedJson(content)) return false;
    if (value.isError) return fields(content, ['code']) && errorCode(content.code);
    const d = content.diff;
    return fields(d, ['truncated', 'changes', 'linesBefore', 'linesAfter'], ['tooLarge'])
      && typeof d.truncated === 'boolean' && tokenCount(d.linesBefore) && tokenCount(d.linesAfter)
      && (!Object.hasOwn(d, 'tooLarge') || d.tooLarge === true)
      && list(d.changes, MAX_WRITE_TOOL_CONTENT, change => fields(change, ['op', 'text', 'ending'])
        && ['add', 'remove'].includes(change.op) && string(change.text, MAX_WRITE_TOOL_CONTENT, true)
        && ['CRLF', 'LF', 'CR', 'none'].includes(change.ending))
      && (!Object.hasOwn(content, 'buffer') || typeof content.buffer === 'boolean');
  } catch { return false; }
}

export function isByokEnvelope(value) {
  const required = value?.type === 'byok.event' ? ['version', 'id', 'type', 'sequence', 'payload'] : ['version', 'id', 'type', 'payload'];
  if (!fields(value, required) || value.version !== 1 || !guid(value.id) || !boundedJson(value)) return false;
  if (value.type === 'byok.event' && (!Number.isSafeInteger(value.sequence) || value.sequence < 1)) return false;
  const p = value.payload;
  switch (value.type) {
    case 'error': return fields(p, ['code']) && errorCode(p.code);
    case 'byok.list.result': return fields(p, ['configurations', 'remainingBudget']) && probeBudget(p.remainingBudget)
      && list(p.configurations, 256, configuration) && unique(p.configurations, item => item.id);
    case 'byok.configure.result': case 'byok.discover.result':
      return fields(p, ['configuration', 'models', 'credentialValid', 'code'], ['retryAfter']) && configuration(p.configuration)
        && list(p.models, 8192, isModel) && unique(p.models, item => item.id)
        && (p.credentialValid === null || typeof p.credentialValid === 'boolean') && (p.code === null || errorCode(p.code))
        && (!Object.hasOwn(p, 'retryAfter') || p.retryAfter === null || (Number.isSafeInteger(p.retryAfter) && p.retryAfter >= 0));
    case 'byok.select.result': return fields(p, ['configuration']) && configuration(p.configuration);
    case 'byok.override.result': return fields(p, ['model']) && isModel(p.model);
    case 'byok.probe.result': return fields(p, ['model', 'supported', 'remainingBudget'], ['retryAfter']) && isModel(p.model)
      && (p.supported === null || typeof p.supported === 'boolean') && probeBudget(p.remainingBudget)
      && (!Object.hasOwn(p, 'retryAfter') || p.retryAfter === null || (Number.isSafeInteger(p.retryAfter) && p.retryAfter >= 0));
    case 'byok.history.list.result': return fields(p, ['conversations']) && list(p.conversations, 256, summary) && unique(p.conversations, item => item.id);
    case 'byok.history.open.result': return fields(p, ['conversation', 'offset', 'total', 'turns']) && summary(p.conversation)
      && Number.isInteger(p.offset) && p.offset >= 0 && Number.isInteger(p.total) && p.total === p.conversation.turnCount && p.offset <= p.total
      && list(p.turns, 10, historyTurn) && p.offset + p.turns.length <= p.total && p.turns.every((turn, index) => turn.index === p.offset + index);
    case 'byok.history.rename.result': return fields(p, ['conversation']) && summary(p.conversation);
    case 'byok.history.new.result': return fields(p, ['created']) && p.created === true;
    case 'byok.history.delete.result': return fields(p, ['deleted']) && p.deleted === true;
    case 'byok.chat.result': return fields(p, ['completed', 'conversation']) && typeof p.completed === 'boolean' && summary(p.conversation);
    case 'byok.cancel.result': return fields(p, ['cancelled']) && p.cancelled === true;
    case 'byok.approval.respond.result': return fields(p, ['accepted']) && p.accepted === true;
    case 'byok.context.capture.result': return fields(p, ['context']) && context(p.context);
    case 'byok.context.release.result': return fields(p, ['released']) && p.released === true;
    case 'byok.tool.read.result': return readToolResult(p);
    case 'byok.tool.write.result': return writeToolResult(p);
    case 'byok.tool.write.status.result': return fields(p, ['pending']) && pendingWrites(p.pending);
    case 'byok.tool.write.undo.result': return fields(p, ['undone', 'pending']) && p.undone === true && pendingWrites(p.pending);
    case 'byok.event': return isByokEvent(p);
    default: return false;
  }
}

function validRequest(type, p) {
  switch (type) {
    case 'byok.list': case 'byok.cancel': case 'byok.history.new': return fields(p, []);
    case 'byok.context.capture': return fields(p, ['kind'], ['path']) && contextKinds.includes(p.kind)
      && (!Object.hasOwn(p, 'path') || (p.kind === 'file' && string(p.path, 1024, true)));
    case 'byok.context.release': return fields(p, ['ids']) && contextIds(p.ids);
    case 'byok.tool.read': return isReadToolRequest(p);
    case 'byok.tool.write': return isWriteToolRequest(p);
    case 'byok.tool.write.undo': case 'byok.tool.write.status': return fields(p, []);
    case 'byok.history.list': return fields(p, ['query']) && string(p.query, 256, true);
    case 'byok.history.open': return fields(p, ['conversationId', 'offset', 'limit']) && guid(p.conversationId)
      && Number.isInteger(p.offset) && p.offset >= 0 && p.offset <= 10000 && Number.isInteger(p.limit) && p.limit >= 1 && p.limit <= 10;
    case 'byok.history.rename': return fields(p, ['conversationId', 'title']) && guid(p.conversationId) && string(p.title, 160);
    case 'byok.history.delete': return fields(p, ['conversationId', 'confirm']) && guid(p.conversationId) && p.confirm === true;
    case 'byok.configure': return fields(p, ['id', 'providerId', 'kind', 'baseUrl', 'key'], ['deploymentNames'])
      && guid(p.id) && string(p.providerId, 64) && PROVIDER_KINDS.includes(p.kind) && validUrl(p.baseUrl)
      && string(p.key, 4096, true) && !/[\r\n]/.test(p.key) && (!Object.hasOwn(p, 'deploymentNames') || deployments(p.deploymentNames));
    case 'byok.discover': return fields(p, ['configurationId']) && guid(p.configurationId);
    case 'byok.probe': return fields(p, ['configurationId', 'capability', 'consent']) && guid(p.configurationId)
      && PROBE_CAPABILITIES.includes(p.capability) && p.consent === true;
    case 'byok.select': return fields(p, ['configurationId', 'modelId']) && guid(p.configurationId) && string(p.modelId, 512);
    case 'byok.override': return fields(p, ['configurationId', 'modelId', 'vision', 'tools', 'reasoning'])
      && guid(p.configurationId) && string(p.modelId, 512) && ['vision', 'tools', 'reasoning'].every(key => typeof p[key] === 'boolean');
    case 'byok.chat': return fields(p, ['configurationId', 'text', 'effort'], ['image', 'contextIds', 'readTools', 'writeTools', 'terminal', 'mode', 'budget']) && guid(p.configurationId)
      && (!Object.hasOwn(p, 'mode') || ['Ask', 'Plan', 'Agent'].includes(p.mode))
      && (p.mode === 'Agent' || (p.writeTools !== true && p.terminal !== true))
      && (!Object.hasOwn(p, 'readTools') || typeof p.readTools === 'boolean')
      && (!Object.hasOwn(p, 'writeTools') || typeof p.writeTools === 'boolean')
      && (!Object.hasOwn(p, 'terminal') || typeof p.terminal === 'boolean')
      && (!Object.hasOwn(p, 'budget') || (fields(p.budget, ['maxRounds', 'maxOutputTokens', 'maxInvocations'])
        && Number.isSafeInteger(p.budget.maxRounds) && p.budget.maxRounds >= 1 && p.budget.maxRounds <= 8
        && Number.isSafeInteger(p.budget.maxOutputTokens) && p.budget.maxOutputTokens >= 1024 && p.budget.maxOutputTokens <= 32768
        && Number.isSafeInteger(p.budget.maxInvocations) && p.budget.maxInvocations >= 1 && p.budget.maxInvocations <= 32))
      && (!Object.hasOwn(p, 'contextIds') || contextIds(p.contextIds))
      && string(p.text, 4096) && EFFORTS.includes(p.effort) && (!Object.hasOwn(p, 'image') || (fields(p.image, ['mediaType', 'base64Data'])
        && IMAGE_TYPES.includes(p.image.mediaType) && string(p.image.base64Data, Math.ceil(MAX_IMAGE_BYTES / 3) * 4)
        && p.image.base64Data.length % 4 === 0 && /^[A-Za-z0-9+/]+={0,2}$/.test(p.image.base64Data)
        && p.image.base64Data.length / 4 * 3 - (p.image.base64Data.endsWith('==') ? 2 : p.image.base64Data.endsWith('=') ? 1 : 0) <= MAX_IMAGE_BYTES));
    case 'byok.approval.respond': return fields(p, ['requestId', 'decision']) && guid(p.requestId) && APPROVAL_DECISIONS.includes(p.decision);
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
    if (!['byok.list', 'byok.configure', 'byok.discover', 'byok.select', 'byok.override', 'byok.probe', 'byok.chat', 'byok.cancel',
      'byok.history.list', 'byok.history.open', 'byok.history.new', 'byok.history.rename', 'byok.history.delete',
      'byok.context.capture', 'byok.context.release', 'byok.tool.read', 'byok.tool.write', 'byok.tool.write.undo', 'byok.tool.write.status',
      'byok.approval.respond'].includes(type)) return Promise.reject(new ByokError('unsupported_type'));
    if (!validRequest(type, payload) || (type === 'byok.probe' && expectedModelId !== undefined && !string(expectedModelId, 512))) return Promise.reject(new ByokError('invalid_message'));
    if (this.pending.size) {
      const active = [...this.pending.values()];
      if (type === 'byok.cancel') {
        if (active.some(value => value.type === 'byok.cancel')) return Promise.reject(new ByokError('bridge_busy'));
      } else if (type === 'byok.approval.respond') {
        // A decision may only reach the single chat that is waiting for it.
        const approval = active.length === 1 && active[0].type === 'byok.chat' ? active[0].approvals.get(payload.requestId) : undefined;
        if (!approval || approval.responded) return Promise.reject(new ByokError('bridge_busy'));
        approval.responded = true;
      } else return Promise.reject(new ByokError('bridge_busy'));
    }
    if (this.usedIds.size >= 16384) return Promise.reject(new ByokError('bridge_busy'));
    const id = this.newId();
    if (!guid(id) || this.usedIds.has(id)) return Promise.reject(new ByokError('invalid_message'));
    // Do not evict IDs during this bridge's lifetime: delayed completions cannot become new requests.
    this.usedIds.add(id);
    const expected = type === 'byok.configure' ? { configurationId: payload.id, providerId: payload.providerId, kind: payload.kind }
      : { configurationId: payload.configurationId, modelId: type === 'byok.probe' ? expectedModelId : payload.modelId,
        conversationId: payload.conversationId, offset: payload.offset, limit: payload.limit, kind: payload.kind, name: payload.name };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.finish(id, new ByokError('timeout')), this.timeoutMs);
      const writeTools = payload.writeTools === true;
      const terminalTools = payload.terminal === true;
      const toolNames = new Set(terminalTools ? [...READ_TOOL_NAMES, ...WRITE_TOOL_NAMES, 'apply_diff', ...COMMAND_TOOL_NAMES]
        : writeTools ? [...READ_TOOL_NAMES, ...WRITE_TOOL_NAMES, 'apply_diff'] : READ_TOOL_NAMES);
      this.pending.set(id, { type, expected, onEvent, resolve, reject, timer,
        readTools: payload.readTools === true || writeTools || terminalTools, writeTools, terminalTools, toolNames,
        approvals: new Map(), terminal: null, tools: new Map(), streamSize: 0, eventCount: 0, nextSequence: 1 });
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
    if (message.type === 'error') { this.finish(message.id, new ByokError(message.payload.code, message.payload.retryAfter)); return true; }
    const p = message.payload;
    if (message.type === 'byok.event') {
      if (pending.type !== 'byok.chat' || pending.terminal || message.sequence !== pending.nextSequence) { this.finish(message.id, new ByokError('invalid_response')); return false; }
      pending.nextSequence++;
      if (++pending.eventCount > 32768) { this.finish(message.id, new ByokError('stream_too_large')); return false; }
      if (p.kind === 'ToolCallStart') {
        if (pending.tools.has(p.id) || pending.tools.size >= 128) { this.finish(message.id, new ByokError('invalid_response')); return false; }
        pending.tools.set(p.id, { state: 'started', name: p.name });
      } else if (p.kind === 'ToolCallArgsDelta' || p.kind === 'ToolCallEnd') {
        if (pending.tools.get(p.id)?.state !== 'started') { this.finish(message.id, new ByokError('invalid_response')); return false; }
        if (p.kind === 'ToolCallEnd') pending.tools.get(p.id).state = 'ended';
      } else if (p.kind === 'ToolExecution') {
        const call = pending.tools.get(p.id);
        const validState = p.status === 'Running' ? call?.state === 'ended' && pending.toolNames.has(call?.name)
          : p.status === 'Cancelled' ? ['ended', 'Running'].includes(call?.state)
          : p.status === 'Failed' ? p.isError && ['ended', 'Running'].includes(call?.state)
          : call?.state === 'Running' && pending.toolNames.has(call?.name);
        if (!pending.readTools || !validState) {
          this.finish(message.id, new ByokError('invalid_response')); return false;
        }
        call.state = p.status;
      } else if (p.kind === 'ApprovalRequest') {
        const permitted = p.category === 'ExecuteCommand' ? pending.terminalTools : pending.writeTools;
        if (!permitted || pending.approvals.has(p.id) || pending.approvals.size >= 8) {
          this.finish(message.id, new ByokError('invalid_response')); return false;
        }
        pending.approvals.set(p.id, { category: p.category, responded: false });
      }
      pending.streamSize += (p.text?.length || p.chunk?.length || p.content?.length || 0) + (p.arguments ? JSON.stringify(p.arguments).length : 0);
      if (pending.streamSize > MAX_WIRE_SIZE) { this.finish(message.id, new ByokError('stream_too_large')); return false; }
      // ToolUse ends a provider round, not the native automatic operation or its shared gate.
      if (p.kind === 'Error' || (p.kind === 'Done' && !(pending.readTools && p.reason === 'ToolUse'))) pending.terminal = p.kind;
      try { pending.onEvent(p.kind === 'Error' ? { kind: 'Error', code: safeErrorCode(p.code), retryAfter: p.retryAfter ?? null } : p); }
      catch { this.finish(message.id, new ByokError('invalid_response')); return false; }
      return true;
    }
    const expected = pending.expected;
    if (message.type !== `${pending.type}.result`
      || (p.configuration && expected.configurationId && p.configuration.id !== expected.configurationId)
      || (pending.type === 'byok.configure' && (p.configuration.providerId !== expected.providerId || p.configuration.kind !== expected.kind))
      || (pending.type === 'byok.select' && p.configuration.selectedModelId !== expected.modelId)
      || (pending.type === 'byok.context.capture' && p.context.kind !== expected.kind)
      || (['byok.tool.read', 'byok.tool.write'].includes(pending.type) && p.name !== expected.name)
      || (pending.type === 'byok.override' && p.model.id !== expected.modelId)
      || (pending.type === 'byok.probe' && expected.modelId !== undefined && p.model.id !== expected.modelId)
      || (p.conversation && expected.conversationId && p.conversation.id !== expected.conversationId)
      || (pending.type === 'byok.history.open' && (p.offset !== expected.offset || p.turns.length !== Math.min(expected.limit, p.total - p.offset)))
      || (pending.type === 'byok.chat' && (p.conversation.configurationId !== expected.configurationId || (pending.terminal === 'Error' && p.completed)
        || [...pending.approvals.values()].some(approval => !approval.responded)))) {
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
