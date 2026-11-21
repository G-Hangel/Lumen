import { VirtualHistory } from './virtual-history.mjs';
import { setMode } from './theme.mjs';
import { ByokBridge, ByokError, PROVIDER_KINDS, PROBE_CAPABILITIES, PROBE_PROVIDER_KINDS, MAX_PROBE_BUDGET, IMAGE_TYPES, MAX_IMAGE_BYTES, MAX_CONTEXTS, MAX_CONTEXT_TEXT, MAX_CONTEXT_TOTAL, READ_TOOL_NAMES, isReadToolRequest, WRITE_TOOL_NAMES, isWriteToolRequest, normalizeBaseUrl, safeErrorCode } from './byok-bridge.mjs';

export const DEFAULT_BASE_URLS = Object.freeze({
  OpenAI: 'https://api.openai.com/v1/', OpenRouter: 'https://openrouter.ai/api/v1/',
  Anthropic: 'https://api.anthropic.com/v1/', Gemini: 'https://generativelanguage.googleapis.com/',
  Ollama: 'http://127.0.0.1:11434/', Azure: '', LMStudio: 'http://127.0.0.1:1234/'
});
const capabilityNames = { vision: 'Vision', tools: 'Tools', reasoning: 'Reasoning' };
const MAX_PROMPT_LENGTH = 262144;
const capabilityLabels = { vision: 'byokVision', tools: 'byokTools', reasoning: 'byokReasoning' };
const errorLabels = {
  invalid_key: 'byokErrorInvalidKey', permission_denied: 'byokErrorPermission', invalid_url: 'byokErrorUrl', unsafe_url: 'byokErrorUrl',
  rate_limited: 'byokErrorRate', insufficient_credit: 'byokErrorCredit', timeout: 'byokErrorTimeout',
  bridge_unavailable: 'byokErrorBridge', bridge_busy: 'byokErrorBusy', invalid_message: 'byokErrorFormat', invalid_response: 'byokErrorFormat',
  invalid_image_type: 'byokErrorImageType', image_too_large: 'byokErrorImageSize', vision_not_supported: 'byokErrorVision', network_error: 'byokErrorNetwork',
  history_corrupt: 'historyErrorCorrupt', history_too_large: 'historyErrorSize', history_write_failed: 'historyErrorWrite', conversation_not_found: 'historyErrorMissing',
  context_unavailable: 'contextErrorUnavailable', context_outside_workspace: 'contextErrorOutside',
  context_too_large: 'contextErrorSize', context_stale: 'contextErrorStale',
  unknown_tool: 'readToolsErrorArguments', invalid_arguments: 'readToolsErrorArguments', invalid_path: 'readToolsErrorPath',
  path_unavailable: 'readToolsErrorPath', outside_workspace: 'readToolsErrorOutside', workspace_unavailable: 'readToolsErrorWorkspace',
  output_too_large: 'readToolsErrorSize', file_too_large: 'readToolsErrorSize', binary_file: 'readToolsErrorBinary',
  tool_budget_exceeded: 'byokErrorToolBudget', skill_unavailable: 'skillsUnavailable',
  file_exists: 'writeToolsErrorExists', undo_failed: 'writeToolsErrorUndo',
  undo_conflict: 'writeToolsErrorConflict', edit_conflict: 'writeToolsErrorConflict', journal_limit: 'readToolsErrorBudget', mode_denied: 'writeToolsErrorMode',
  approval_denied: 'approvalErrorDenied', command_blocked: 'commandErrorBlocked', command_timeout: 'commandErrorTimeout', command_failed: 'commandErrorFailed'
};
export const errorLabel = code => errorLabels[safeErrorCode(code)] || 'byokErrorUnknown';

export function parseMentions(text) {
  const mentions = [];
  const seen = new Set();
  for (const match of text.matchAll(/(?:^|\s)@([^\s@]+)/g)) {
    const token = match[1];
    const kind = /^(solução|solution)$/i.test(token) ? 'solution' : 'file';
    const key = kind === 'solution' ? 'solution' : `file:${token.replace(/^\.\//, '').toLowerCase()}`;
    if (seen.has(key)) continue;
    seen.add(key);
    mentions.push({ kind, token, ...(kind === 'file' ? { path: token } : {}) });
  }
  return mentions;
}

export function capabilityState(model, capability) {
  const origin = model.origins.find(item => item.capability === capabilityNames[capability]);
  return {
    value: model[capability] ? true : origin ? false : null,
    source: origin?.source || null, confidence: origin?.confidence || null,
    unverified: !origin || origin.source === 'Heuristic' || origin.confidence === 'Low'
  };
}

export async function readImageAttachment(file) {
  if (!file || !IMAGE_TYPES.includes(file.type)) throw new ByokError('invalid_image_type');
  if (file.size > MAX_IMAGE_BYTES) throw new ByokError('image_too_large');
  if (!Number.isSafeInteger(file.size) || file.size <= 0) throw new ByokError('invalid_image_type');
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (bytes.length !== file.size) throw new ByokError('invalid_image_type');
  const starts = signature => signature.every((byte, index) => bytes[index] === byte);
  const matches = file.type === 'image/png' ? starts([137, 80, 78, 71, 13, 10, 26, 10])
    : file.type === 'image/jpeg' ? starts([255, 216, 255])
    : file.type === 'image/gif' ? starts([71, 73, 70, 56]) && (bytes[4] === 55 || bytes[4] === 57) && bytes[5] === 97
    : starts([82, 73, 70, 70]) && bytes[8] === 87 && bytes[9] === 69 && bytes[10] === 66 && bytes[11] === 80;
  if (!matches) throw new ByokError('invalid_image_type');
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 16384) binary += String.fromCharCode(...bytes.subarray(offset, offset + 16384));
  return { mediaType: file.type, base64Data: btoa(binary) };
}

// Main owns handshake, localization and safe Markdown. This module never synthesizes a native connection.
export function startByok({ transport, connected = () => false, text, renderMarkdown } = {}) {
  const node = id => document.getElementById(`byok-${id}`);
  const panel = node('panel');
  node('mode').value = 'Ask';
  node('permission').value = 'Interactive';
  setMode(node('mode').value);
  let connectedState = Boolean(connected()), disposed = false, busy = false, imageLoading = false, cancelling = false;
  let cancelRequested = false, cancelRequest = null;
  let configurations = [], models = [], configurationId = '', selectedModelId = '', loaded = false;
  let statusKey = connectedState ? 'byokReady' : 'byokDisconnected', errorCode = null, retryAfterSeconds = null, attachment = null, attachmentGeneration = 0;
  let streaming = false, needsCancel = false, probing = false, remainingBudget = null;
  const listeners = [];
  let contexts = [], contextBusy = false;
  let skillItems = [], skillDiagnostics = [], skillsLoaded = false, skillsLoading = false, skillRefreshRequested = false, suggestionIndex = 0;
  const selectedSkills = new Set();
  let agentItems = [], agentDiagnostics = [], agentsLoaded = false, agentsLoading = false, agentRefreshRequested = false;
  let diagLevel = 'Error', diagLoaded = false, diagBusy = false;
  let readBusy = false, readStatusKey = 'readToolsIdle', readErrorCode = null;
  function renderReadStatus() {
    node('read-status').textContent = readErrorCode
      ? `${text('readToolsFailed')} ${text(readErrorCode === 'unknown' ? 'readToolsErrorUnknown' : errorLabel(readErrorCode))}` : text(readStatusKey);
    node('read-status').className = readErrorCode ? 'error' : 'secondary';
  }
  function changeReadTool() {
    const name = node('read-name').value;
    node('read-lines').hidden = name !== 'read_file';
    node('read-search').hidden = name === 'read_file';
    node('read-glob-group').hidden = !['find_files', 'grep'].includes(name);
    node('read-pattern-group').hidden = name !== 'grep';
    node('read-glob').required = name === 'find_files';
    node('read-pattern').required = name === 'grep';
    for (const id of ['start', 'end']) node(`read-${id}`).disabled = name !== 'read_file';
    node('read-max').disabled = name === 'read_file';
    node('read-glob').disabled = !['find_files', 'grep'].includes(name);
    node('read-pattern').disabled = name !== 'grep';
  }
  async function readDisk(event) {
    event.preventDefault();
    if (!online() || busy || imageLoading || needsCancel || cancelling) return;
    const name = node('read-name').value, args = { path: node('read-path').value };
    for (const [id, key] of (name === 'read_file' ? [['start', 'startLine'], ['end', 'endLine']] : [['max', 'maxResults']])) {
      if (node(`read-${id}`).value !== '') args[key] = Number(node(`read-${id}`).value);
    }
    if (name === 'find_files' || (name === 'grep' && node('read-glob').value !== '')) args.glob = node('read-glob').value;
    if (name === 'grep') args.pattern = node('read-pattern').value;
    readBusy = true; readErrorCode = null; readStatusKey = 'readToolsReading';
    node('read-result').textContent = ''; node('read-result').hidden = true; renderReadStatus();
    try {
      await operate(async () => {
        try {
          if (!isReadToolRequest({ name, arguments: args })) throw new ByokError('invalid_arguments');
          const result = await bridge.request('byok.tool.read', { name, arguments: args });
          if (disposed) return;
          // Disk output stays separate from chat/context and is never passed to the Markdown renderer.
          node('read-result').textContent = result.content; node('read-result').hidden = false;
          if (result.isError) readErrorCode = safeErrorCode(JSON.parse(result.content).code);
          else readStatusKey = 'readToolsSucceeded';
          setStatus('byokReady');
        } catch (error) {
          if (['timeout', 'invalid_response'].includes(error.code)) needsCancel = true;
          if (error.code === 'cancelled') readStatusKey = 'byokCancelled';
          else readErrorCode = safeErrorCode(error.code);
          throw error;
        } finally { if (!disposed) renderReadStatus(); }
      });
    } finally {
      readBusy = false;
      if (!disposed) {
        updateControls();
        if (node('read-tools').open && !cancelling) node('read-status').focus({ preventScroll: true });
      }
    }
  }
  let writeBusy = false, writePending = null, writeRefreshRequested = false;
  let writeStatusKey = 'writeToolsIdle', writeErrorCode = null, writeDiff = null;
  const writeAllowed = () => online() && !busy && !imageLoading && !needsCancel && !cancelling;
  function renderWriteStatus() {
    const label = writeErrorCode === 'unknown' ? 'writeToolsErrorUnknown'
      : ['invalid_arguments', 'unknown_tool'].includes(writeErrorCode) ? 'writeToolsErrorArguments' : errorLabel(writeErrorCode);
    node('write-status').textContent = writeErrorCode
      ? `${text('writeToolsFailed')} ${text(label)}` : text(writeStatusKey);
    node('write-status').className = writeErrorCode ? 'error' : 'secondary';
    node('write-pending').textContent = writePending === null ? text('writeToolsPendingUnknown') : text('writeToolsPending', { count: writePending });
    node('write-diff-note').hidden = !writeDiff;
    if (writeDiff) {
      node('write-diff-note').textContent = text(writeDiff.tooLarge ? 'writeToolsDiffTooLarge'
        : writeDiff.truncated ? 'writeToolsDiffTruncated' : 'writeToolsDiffFull', { before: writeDiff.linesBefore, after: writeDiff.linesAfter });
    }
  }
  function clearWriteDiff() {
    writeDiff = null; node('write-diff').textContent = ''; node('write-diff').hidden = true;
  }
  async function writeOperation(type, payload, statusKey, focus = true) {
    if (!writeAllowed()) return;
    writeBusy = true; writeErrorCode = null; writeStatusKey = statusKey;
    renderWriteStatus();
    try {
      await operate(async () => {
        let sent = false;
        try {
          if (type === 'byok.tool.write' && !isWriteToolRequest(payload)) throw new ByokError('invalid_arguments');
          sent = true;
          const result = await bridge.request(type, payload);
          if (disposed) return;
          writePending = result.pending;
          if (type === 'byok.tool.write') {
            const content = JSON.parse(result.content);
            if (result.isError) writeErrorCode = safeErrorCode(content.code);
            else {
              writeDiff = content.diff;
              // Prefix every displayed line, including embedded newlines, so model/file text remains inert.
              node('write-diff').textContent = writeDiff.changes.map(change => change.text.split('\n')
                .map(line => `${change.op === 'add' ? '+' : '-'} ${line} [${change.ending}]`).join('\n')).join('\n');
              node('write-diff').hidden = false; writeStatusKey = 'writeToolsSucceeded';
            }
          } else if (type === 'byok.tool.write.undo') {
            clearWriteDiff(); writeStatusKey = 'writeToolsUndone';
          } else writeStatusKey = 'writeToolsChecked';
          setStatus('byokReady');
        } catch (error) {
          // Transport errors cannot prove whether a write/partial undo happened. Only native status can reconcile it.
          if (sent) writePending = null;
          if (['timeout', 'invalid_response'].includes(error.code)) needsCancel = true;
          if (error.code === 'cancelled') writeStatusKey = 'byokCancelled';
          else writeErrorCode = safeErrorCode(error.code);
          throw error;
        } finally { if (!disposed) renderWriteStatus(); }
      });
    } finally {
      writeBusy = false;
      if (!disposed) {
        updateControls();
        if (focus && node('write-tools').open && !cancelling) node('write-status').focus({ preventScroll: true });
      }
    }
  }
  async function applyWrite(event) {
    event.preventDefault();
    if (!writeAllowed()) return;
    clearWriteDiff();
    await writeOperation('byok.tool.write', { name: node('write-name').value,
      arguments: { path: node('write-path').value, content: node('write-content').value } }, 'writeToolsApplying');
  }
  async function undoWrites() {
    if (!writeAllowed() || writePending === null || writePending <= 0) return;
    await writeOperation('byok.tool.write.undo', {}, 'writeToolsUndoing');
  }
  function pumpWriteStatus() {
    if (!writeRefreshRequested || !node('write-tools').open || !writeAllowed()) return;
    writeRefreshRequested = false;
    void writeOperation('byok.tool.write.status', {}, 'writeToolsChecking', false);
  }
  function requestWriteStatus() {
    if (!node('write-tools').open) return;
    writeRefreshRequested = true; pumpWriteStatus();
  }
  const contextSize = () => contexts.reduce((sum, item) => sum + item.text.length, 0);
  const contextWithinBounds = () => contexts.length <= MAX_CONTEXTS && contextSize() <= MAX_CONTEXT_TOTAL
    && contexts.every(item => item.text.length <= MAX_CONTEXT_TEXT);
  const canCapture = () => online() && !busy && !imageLoading && !needsCancel && !cancelling
    && contexts.length < MAX_CONTEXTS && contextSize() < MAX_CONTEXT_TOTAL;
  let activeConversation = null, savedConversations = [], records = [], liveTurn = null, historyLoaded = false;
  const TAB_STORAGE_KEY = 'lumen.sessionTabs.v1';
  let openTabs = readStoredTabs();
  let visible = { start: 0, end: 0 }, deleteTarget = '', historyGeneration = 0, removeTarget = '';
  const failedPages = new Set(), cached = new Map();
  const bridge = new ByokBridge(transport, { connected: () => connectedState && Boolean(connected()) && !disposed });
  const online = () => bridge.available && connectedState && Boolean(connected()) && !disposed;
  const selectedModel = () => models.find(model => model.id === selectedModelId);
  const listen = (target, event, handler) => { target.addEventListener(event, handler); listeners.push(() => target.removeEventListener(event, handler)); };
  const option = (value, label) => { const result = document.createElement('option'); result.value = value; result.textContent = label; return result; };
  const localized = (tag, key, className = '') => {
    const result = document.createElement(tag); result.dataset.byokText = key; result.textContent = text(key); result.className = className; return result;
  };

  function setStatus(key) { statusKey = key; node('status').textContent = text(key); }
  function clearError() {
    errorCode = null; retryAfterSeconds = null; node('error').textContent = ''; node('error').hidden = true;
    if (node('error-action')) node('error-action').hidden = true;
    if (node('history-error-action')) node('history-error-action').hidden = true;
    renderHistoryStatus();
  }
  function showError(code, waitSeconds = null) {
    errorCode = safeErrorCode(code); retryAfterSeconds = waitSeconds;
    node('error').textContent = errorCode === 'rate_limited' && Number.isFinite(waitSeconds)
      ? text('byokErrorRateWait', { seconds: Math.max(1, Math.ceil(waitSeconds)) }) : text(errorLabel(errorCode));
    node('error').hidden = false;
    const actionable = ['invalid_key', 'permission_denied', 'invalid_url', 'unsafe_url'].includes(errorCode);
    if (node('error-action')) node('error-action').hidden = !actionable;
    if (node('history-error-action')) node('history-error-action').hidden = !actionable;
    renderHistoryStatus();
    if (errorCode === 'cancelled') { clearError(); setStatus('byokCancelled'); }
  }
  function removeAttachment() {
    attachment = null; attachmentGeneration++; node('attachment').hidden = true;
    node('attachment').textContent = ''; node('remove-image').hidden = true;
    const announcement = document.getElementById('announcement');
    if (announcement && text) announcement.textContent = text('byokRemoveImage');
  }

  function renderContexts() {
    node('context-list').replaceChildren();
    node('context-count').textContent = text('contextCount', { count: contexts.length, max: MAX_CONTEXTS, chars: contextSize(), total: MAX_CONTEXT_TOTAL });
    for (const item of contexts) {
      const row = document.createElement('li');
      const preview = document.createElement('details');
      const title = document.createElement('summary'); title.textContent = `${item.label} · ${text('contextChars', { count: item.text.length })}`;
      const content = document.createElement('pre'); content.className = 'byok-plain'; content.tabIndex = 0; content.textContent = item.text;
      const remove = localized('button', 'contextRemove', 'text-button'); remove.type = 'button';
      remove.setAttribute('aria-label', text('contextRemoveLabel', { label: item.label }));
      remove.disabled = !online() || busy || imageLoading || needsCancel || cancelling;
      remove.addEventListener('click', () => releaseContext(item.id));
      preview.append(title, content); row.append(preview, remove); node('context-list').append(row);
    }
  }
  async function contextOperation(task) {
    contextBusy = true;
    try {
      await operate(async () => {
        try { await task(); }
        catch (error) {
          if (['timeout', 'invalid_response'].includes(error.code)) needsCancel = true;
          throw error;
        }
      });
    } finally { contextBusy = false; if (!disposed) updateControls(); }
  }
  async function captureContext(kind, path) {
    if (!canCapture()) return;
    await contextOperation(async () => {
      const result = await bridge.request('byok.context.capture', { kind, ...(kind === 'file' && path !== undefined ? { path } : {}) });
      if (disposed) return;
      if (contexts.some(item => item.id.toLowerCase() === result.context.id.toLowerCase())) throw new ByokError('invalid_response');
      contexts.push(result.context); renderContexts();
      // A malformed aggregate remains visible/removable, never silently sent or released.
      if (!contextWithinBounds()) throw new ByokError('context_too_large');
      setStatus('contextCaptured');
    });
  }
  async function releaseContext(id) {
    if (!online() || busy || imageLoading || needsCancel || cancelling || !contexts.some(item => item.id === id)) return;
    await contextOperation(async () => {
      await bridge.request('byok.context.release', { ids: [id] });
      if (disposed) return;
      contexts = contexts.filter(item => item.id !== id); renderContexts(); setStatus('contextReleased');
    });
  }
  function attachReference() {
    if (!canCapture()) return;
    const value = node('context-path').value.trim();
    if (/^@(solução|solution)$/i.test(value)) void captureContext('solution');
    else void captureContext('file', value.replace(/^@(arquivo|file)(?:\s+|$)/i, ''));
  }

  function renderProbeBudget() {
    node('probe-budget').textContent = text('byokProbeBudget', { remaining: remainingBudget ?? text('byokUnknown'), max: MAX_PROBE_BUDGET });
  }
  function clearProbeConsent() { node('probe-consent').checked = false; }
  function probeEligible() {
    const config = configurations.find(item => item.id === configurationId);
    return online() && !busy && !imageLoading && !needsCancel && !cancelling && remainingBudget > 0
      && Boolean(selectedModel()) && config?.selectedModelId === selectedModelId && PROBE_PROVIDER_KINDS.includes(config?.kind)
      && node('providers').value === configurationId && node('models').value === selectedModelId;
  }
  function canProbe() { return probeEligible() && node('probe-consent').checked === true && PROBE_CAPABILITIES.includes(node('probe-capability').value); }

  function renderActiveModel() {
    const model = selectedModel();
    node('active-model').textContent = model ? text('byokActiveModel', { model: model.name }) : text('byokNoModel');
  }
  function syncEmpty() { node('empty').hidden = records.length > 0 || Boolean(liveTurn); }
  function renderHistoryStatus() {
    node('history-status').textContent = errorCode && errorCode !== 'cancelled' ? text(errorLabel(errorCode))
      : activeConversation ? text('historyActive', { title: activeConversation.title, count: activeConversation.turnCount }) : '';
  }
  function renderSaved() {
    const selected = node('history-list').value;
    node('history-list').replaceChildren(option('', text(savedConversations.length ? 'historyChoose' : 'historyEmpty')),
      ...savedConversations.map(item => option(item.id, `${item.title} · ${item.modelId} · ${item.turnCount}`)));
    node('history-list').value = savedConversations.some(item => item.id === selected) ? selected : activeConversation?.id || '';
    node('history-name').value = savedConversations.find(item => item.id === node('history-list').value)?.title || '';
  }
  function rememberSummary(summary) {
    activeConversation = summary;
    savedConversations = [summary, ...savedConversations.filter(item => item.id !== summary.id)].slice(0, 256);
    const tab = openTabs.find(item => item.id === summary.id) || openTabs.find(item => item.id === null);
    if (tab) { tab.id = summary.id; tab.title = summary.title; tab.modelId = summary.modelId; tab.turnCount = summary.turnCount; }
    else openTabs.push({ id: summary.id, title: summary.title, modelId: summary.modelId, turnCount: summary.turnCount });
    renderSaved(); node('history-list').value = summary.id; node('history-name').value = summary.title;
    renderHistoryStatus(); renderTabs(); persistTabs();
  }
  function resetHistory() {
    historyGeneration++; activeConversation = null; records = []; liveTurn = null; cached.clear(); failedPages.clear();
    node('live').replaceChildren(); history.setItems(records); syncEmpty(); renderHistoryStatus(); renderTabs();
  }
  function cacheTurns(turns) {
    for (const item of turns) { records[item.index] = item; cached.delete(item.index); cached.set(item.index, true); }
    for (const index of cached.keys()) {
      if (cached.size <= 40) break;
      if (index >= visible.start && index < visible.end) continue;
      cached.delete(index); records[index] = null;
    }
  }
  async function pumpHistory() {
    if (!online() || busy || imageLoading || needsCancel || cancelling || !activeConversation) return;
    const index = records.findIndex((item, index) => !item && index >= visible.start && index < visible.end && !failedPages.has(Math.floor(index / 10) * 10));
    if (index < 0) return;
    const offset = Math.floor(index / 10) * 10, generation = historyGeneration, id = activeConversation.id;
    busy = true; updateControls();
    try {
      const result = await bridge.request('byok.history.open', { conversationId: id, offset, limit: 10 });
      if (disposed || generation !== historyGeneration) return;
      cacheTurns(result.turns); history.invalidate();
    } catch (error) {
      if (!disposed && generation === historyGeneration) { failedPages.add(offset); showError(error.code); }
    } finally {
      busy = false;
      if (!disposed) { updateControls(); queueMicrotask(pumpHistory); }
    }
  }
  async function listHistory() {
    await operate(async () => {
      const result = await bridge.request('byok.history.list', { query: node('history-query').value });
      if (disposed) return;
      savedConversations = result.conversations; historyLoaded = true; renderSaved(); setStatus('byokReady');
    });
  }
  async function openHistoryById(id) {
    if (!id) return;
    await operate(async () => {
      const result = await bridge.request('byok.history.open', { conversationId: id, offset: 0, limit: 10 });
      if (disposed) return;
      resetHistory(); rememberSummary(result.conversation); records = Array(result.total).fill(null);
      cacheTurns(result.turns); node('output').scrollTop = 0; history.setItems(records); syncEmpty(); setStatus('byokReady');
      renderTabs(); persistTabs();
    }, node('output'));
  }
  async function openHistory() {
    const id = node('history-list').value;
    if (id) await openHistoryById(id);
  }
  async function openConversationById(id) {
    if (!online() || busy || !id) return;
    const summary = savedConversations.find(item => item.id === id);
    if (summary && summary.configurationId !== configurationId) {
      const config = configurations.find(item => item.id === summary.configurationId);
      if (!config) { showError('conversation_not_found'); return; }
      node('providers').value = config.id;
      await changeProvider();
    }
    if (summary && summary.modelId && summary.modelId !== selectedModelId) {
      if (!models.some(item => item.id === summary.modelId)) { showError('model_not_found'); return; }
      node('models').value = summary.modelId;
      await chooseModel();
    }
    await openHistoryById(id);
  }
  async function activateTab(tab) {
    if (!online() || busy) return;
    if (!tab.id) { if (!activeConversation) return; await newHistory(); return; }
    if (tab.id === activeConversation?.id) return;
    await openConversationById(tab.id);
  }
  async function closeTab(tab) {
    const index = openTabs.indexOf(tab);
    if (index < 0) return;
    const wasActive = tab.id === activeConversation?.id || (!tab.id && !activeConversation);
    openTabs.splice(index, 1);
    if (wasActive) {
      const next = openTabs.find(item => item.id) || openTabs[0];
      if (next && next.id) await openConversationById(next.id); else await newHistory();
    }
    renderTabs(); persistTabs();
  }
  async function newHistory() {
    await operate(async () => {
      await bridge.request('byok.history.new', {});
      if (disposed) return;
      resetHistory();
      if (!openTabs.some(item => item.id === null)) openTabs.push({ id: null, title: '', modelId: selectedModelId, turnCount: 0 });
      node('history-list').value = ''; node('history-name').value = '';
      renderTabs(); persistTabs(); setStatus('byokReady');
    }, node('message'));
  }
  async function renameHistory(event) {
    event.preventDefault();
    const id = node('history-list').value, title = node('history-name').value.trim();
    if (!id || !title) return;
    await operate(async () => {
      const result = await bridge.request('byok.history.rename', { conversationId: id, title });
      if (disposed) return;
      savedConversations = savedConversations.map(item => item.id === id ? result.conversation : item);
      if (activeConversation?.id === id) activeConversation = result.conversation;
      const tab = openTabs.find(item => item.id === id);
      if (tab) { tab.title = result.conversation.title; tab.modelId = result.conversation.modelId; tab.turnCount = result.conversation.turnCount; }
      renderSaved(); renderHistoryStatus(); renderTabs(); persistTabs(); setStatus('byokReady');
    });
  }
  async function deleteHistory() {
    if (!deleteTarget || node('history-confirmation').hidden) return;
    const id = deleteTarget;
    await operate(async () => {
      await bridge.request('byok.history.delete', { conversationId: id, confirm: true });
      if (disposed) return;
      savedConversations = savedConversations.filter(item => item.id !== id);
      openTabs = openTabs.filter(item => item.id !== id);
      if (activeConversation?.id === id) {
        resetHistory();
        if (!openTabs.some(item => item.id === null)) openTabs.push({ id: null, title: '', modelId: selectedModelId, turnCount: 0 });
      }
      deleteTarget = ''; node('history-confirmation').hidden = true; renderSaved(); renderTabs(); persistTabs(); setStatus('byokReady');
    });
  }
  function scrollPinned() {
    const log = node('output');
    return log.scrollHeight - log.scrollTop - log.clientHeight < 48;
  }
  function updateControls() {
    const enabled = online(), locked = busy || imageLoading || needsCancel || cancelling;
    node('skills-reload').disabled = !enabled || locked;
    for (const input of node('skills-list').querySelectorAll('input')) {
      const skill = skillItems.find(item => item.name === input.dataset.skill);
      input.disabled = !enabled || locked || (input.dataset.action === 'selected' && (!skill.enabled || !skill.modes.includes(node('mode').value) || (selectedSkills.size >= 8 && !selectedSkills.has(skill.name))));
    }
    node('config-fields').disabled = !enabled || locked;
    node('configure-button').disabled = !enabled || locked;
    node('list').disabled = !enabled || locked;
    node('providers').disabled = !enabled || locked || !configurations.length;
    node('discover').disabled = !enabled || locked || !configurationId;
    node('remove').disabled = !enabled || locked || !configurationId;
    for (const id of ['remove-confirm', 'remove-keep']) node(id).disabled = !enabled || locked;
    node('models').disabled = !enabled || locked || !models.length || !configurationId;
    node('model-select').disabled = !enabled || locked || !models.length || !configurationId;
    const model = selectedModel();
    for (const capability of Object.keys(capabilityNames)) node(capability).disabled = !enabled || locked || !model;
    node('apply-override').disabled = !enabled || locked || !model;
    if (node('display-name')) node('display-name').disabled = !enabled || locked || !model;
    if (node('effort-levels')) node('effort-levels').disabled = !enabled || locked || !model;
    if (node('model-enabled')) node('model-enabled').disabled = !enabled || locked || !model;
    node('effort').disabled = !enabled || locked || !model?.reasoning || !model.efforts.length;
    // Model switches now replay the same session, so only the provider must match.
    const matchesHistory = !activeConversation || activeConversation.configurationId === configurationId;
    node('mode').disabled = !enabled || locked;
    node('apply-plan').hidden = node('mode').value !== 'Plan';
    node('apply-plan').disabled = !enabled || locked;
    node('permission').disabled = !enabled || locked;
    node('message').disabled = !enabled || !model || !configurationId || locked || !matchesHistory;
    
    node('remove-image').disabled = locked;
    node('context-path').disabled = !enabled || locked;
    for (const id of ['context-file', 'context-solution', 'context-selection', 'context-output', 'context-reference']) node(id).disabled = !canCapture();
    for (const remove of node('context-list').querySelectorAll('button')) remove.disabled = !enabled || locked;
    node('context').setAttribute('aria-busy', String(contextBusy && busy));
    node('send').disabled = !enabled || locked || !model || !configurationId || !matchesHistory || !contextWithinBounds() || !node('message').value.trim() || node('message').value.length > MAX_PROMPT_LENGTH;
    for (const id of ['history-query', 'history-search', 'history-new']) node(id).disabled = !enabled || locked;
    node('history-list').disabled = !enabled || locked || !savedConversations.length;
    for (const id of ['history-name', 'history-rename', 'history-delete']) node(id).disabled = !enabled || locked || !node('history-list').value;
    for (const id of ['history-confirm', 'history-keep']) node(id).disabled = !enabled || locked;
    for (const id of ['history-first', 'history-last']) node(id).disabled = locked || !records.length;
    if (node('error-action')) node('error-action').disabled = !enabled || locked;
    if (node('history-error-action')) node('history-error-action').disabled = !enabled || locked;
    node('cancel').disabled = !enabled || cancelling || cancelRequested || (!busy && !needsCancel);
    node('read-fields').disabled = !enabled || locked;
    node('read-run').disabled = !enabled || locked;
    node('read-cancel').disabled = node('cancel').disabled;
    node('read-tools').setAttribute('aria-busy', String(readBusy && busy));
    node('write-fields').disabled = !enabled || locked;
    node('write-apply').disabled = !enabled || locked;
    node('write-undo').disabled = !enabled || locked || writePending === null || writePending <= 0;
    node('write-refresh').disabled = !enabled || locked;
    node('write-cancel').disabled = node('cancel').disabled;
    node('write-tools').setAttribute('aria-busy', String(writeBusy && busy));
    if (writeRefreshRequested && enabled && !locked) queueMicrotask(pumpWriteStatus);
    if (skillRefreshRequested && enabled && !locked) queueMicrotask(pumpSkills);
    node('probe-consent').disabled = !probeEligible();
    node('probe-capability').disabled = !probeEligible();
    node('verify').disabled = !canProbe();
    node('probe').setAttribute('aria-busy', String(probing && busy));
    const isAzure = node('kind').value === 'Azure';
    const armFieldsFilled = isAzure && node('azure-subscription').value.trim() && node('azure-resource-group').value.trim() && node('azure-account').value.trim() && node('azure-tenant').value.trim() && node('azure-client').value.trim();
    node('azure-inventory').disabled = !enabled || locked || !configurationId || !isAzure || !armFieldsFilled;
    node('configure').setAttribute('aria-busy', String(busy && !streaming));
    node('chat-form').setAttribute('aria-busy', String(streaming || imageLoading || (contextBusy && busy)));
    node('count').textContent = text('byokCount', { count: node('message').value.length, max: MAX_PROMPT_LENGTH });
    if (!enabled) node('status').textContent = text('byokDisconnected');
  }

  function renderProviders() {
    node('providers').replaceChildren(option('', text(configurations.length ? 'byokChooseProvider' : 'byokEmptyProviders')),
      ...configurations.map(config => option(config.id, `${config.providerId} · ${config.kind}`)));
    node('providers').value = configurationId;
  }
  function modelLabel(model) {
    return `${model.name} (${model.id}) · ${Object.keys(capabilityNames).map(key => {
      const state = capabilityState(model, key);
      return [text(capabilityLabels[key]), text(state.value === null ? 'byokUnknown' : state.value ? 'byokSupported' : 'byokUnsupported'),
        text(state.source ? `byokSource${state.source}` : 'byokUnknown'), text(state.confidence ? `byokConfidence${state.confidence}` : 'byokUnknown'),
        ...(state.unverified ? [text('byokUnverified')] : [])].join(' · ');
    }).join(' / ')}`;
  }
  function renderModels() {
    const visibleModels = models.filter(model => model.enabled !== false || model.id === selectedModelId);
    node('models').replaceChildren(option('', text(visibleModels.length ? 'byokChooseModel' : 'byokEmptyModels')),
      ...visibleModels.map(model => option(model.id, modelLabel(model))));
    node('models').value = selectedModelId;
    node('model-select').replaceChildren(option('', text(visibleModels.length ? 'byokChooseModel' : 'byokEmptyModels')),
      ...visibleModels.map(model => option(model.id, model.name)));
    node('model-select').value = selectedModelId;
  }
  function renderCapabilities(resetOverride = true) {
    node('capabilities').replaceChildren();
    const model = selectedModel();
    if (model) for (const key of Object.keys(capabilityNames)) {
      const state = capabilityState(model, key);
      const item = document.createElement('li');
      const parts = [text(capabilityLabels[key]), text(state.value === null ? 'byokUnknown' : state.value ? 'byokSupported' : 'byokUnsupported'),
        text(state.source ? `byokSource${state.source}` : 'byokUnknown'), text(state.confidence ? `byokConfidence${state.confidence}` : 'byokUnknown')];
      if (state.unverified) parts.push(text('byokUnverified'));
      item.textContent = parts.join(' · '); node('capabilities').append(item);
    }
    for (const key of Object.keys(capabilityNames)) {
      const value = resetOverride ? model ? capabilityState(model, key).value : null : node(key).value === '' ? null : node(key).value === 'true';
      node(key).replaceChildren(option('', text('byokUnknown')), option('true', text('byokSupported')), option('false', text('byokUnsupported')));
      node(key).value = value === null ? '' : String(value);
    }
    if (node('display-name')) {
      node('display-name').value = model ? (model.displayName || model.name || '') : '';
      const checked = new Set(model?.efforts || []);
      for (const input of node('effort-levels').querySelectorAll('input')) input.checked = checked.has(input.value);
      if (node('model-enabled')) node('model-enabled').checked = model ? model.enabled !== false : true;
    }
    const probeCapability = node('probe-capability').value;
    node('probe-capability').replaceChildren(...PROBE_CAPABILITIES.map(value => option(value, text(capabilityLabels[value.toLowerCase()]))));
    node('probe-capability').value = PROBE_CAPABILITIES.includes(probeCapability) ? probeCapability : 'Vision';
    const effort = node('effort').value;
    const supported = model?.reasoning ? model.efforts : [];
    node('effort').replaceChildren(...(supported.length ? supported.map(value => option(value, text(`byokEffort${value}`))) : [option('Off', text('byokEffortDefault'))]));
    node('effort').value = supported.includes(effort) ? effort : supported[0] || 'Off';
    renderActiveModel();
    updateControls();
  }

  function resetSelection() {
    models = []; selectedModelId = ''; clearProbeConsent(); removeAttachment(); renderModels(); renderCapabilities();
  }
  async function operate(task, focusTarget, loadingKey = 'byokLoading') {
    if (!online() || busy || imageLoading || needsCancel || cancelling) return;
    busy = true; clearError(); setStatus(loadingKey); updateControls();
    let succeeded = false;
    try { await task(); succeeded = true; }
    catch (error) { if (!disposed) { setStatus(online() ? 'byokReady' : 'byokDisconnected'); showError(error.code); } }
    finally {
      busy = false; if (!cancelling) cancelRequested = false; clearProbeConsent();
      if (!disposed) {
        updateControls(); queueMicrotask(pumpHistory);
        if (panel.open && !cancelling && (focusTarget || !succeeded)) (errorCode ? node('error') : succeeded ? focusTarget : node('status')).focus({ preventScroll: true });
      }
    }
  }

  function renderSkillSuggestions() {
    const match = /^\/([a-z0-9-]*)$/.exec(node('message').value);
    const matches = match ? skillItems.filter(skill => skill.enabled && skill.modes.includes(node('mode').value) && skill.name.startsWith(match[1])).slice(0, 8) : [];
    suggestionIndex = Math.min(suggestionIndex, Math.max(0, matches.length - 1));
    const container = node('skill-suggestions');
    container.replaceChildren();
    for (const [index, skill] of matches.entries()) {
      const button = document.createElement('button');
      button.type = 'button'; button.id = `byok-skill-option-${index}`; button.tabIndex = -1;
      button.setAttribute('role', 'option'); button.setAttribute('aria-selected', String(index === suggestionIndex));
      button.textContent = `/${skill.name} · ${skill.description}`;
      button.addEventListener('click', () => chooseSkillSuggestion(skill.name));
      container.append(button);
    }
    container.hidden = matches.length === 0;
    if (matches.length) node('message').setAttribute('aria-activedescendant', `byok-skill-option-${suggestionIndex}`);
    else node('message').removeAttribute('aria-activedescendant');
    return matches;
  }

  function chooseSkillSuggestion(name) {
    node('message').value = `/${name} `;
    node('skill-suggestions').hidden = true;
    node('message').removeAttribute('aria-activedescendant');
    node('message').focus({ preventScroll: true });
    updateControls();
  }

  function renderSkills() {
    node('skills-list').replaceChildren();
    for (const skill of skillItems) {
      const item = document.createElement('li');
      const title = document.createElement('strong'); title.textContent = skill.name;
      const description = document.createElement('p'); description.className = 'secondary'; description.textContent = skill.description;
      const source = document.createElement('p'); source.className = 'secondary'; source.textContent = `${skill.source} · ${skill.modes.join(', ')}`;
      const enabledLabel = document.createElement('label'), enabled = document.createElement('input'), enabledText = document.createElement('span');
      enabled.type = 'checkbox'; enabled.checked = skill.enabled; enabled.dataset.skill = skill.name; enabled.dataset.action = 'enabled';
      enabledText.textContent = text('skillsEnabled'); enabledLabel.append(enabled, enabledText);
      enabled.addEventListener('change', () => { void setSkillEnabled(skill.name, enabled.checked); });
      const selectedLabel = document.createElement('label'), selected = document.createElement('input'), selectedText = document.createElement('span');
      selected.type = 'checkbox'; selected.checked = selectedSkills.has(skill.name); selected.dataset.skill = skill.name; selected.dataset.action = 'selected';
      selectedText.textContent = text('skillsSelected'); selectedLabel.append(selected, selectedText);
      selected.addEventListener('change', () => {
        if (selected.checked && selectedSkills.size < 8 && skill.enabled && skill.modes.includes(node('mode').value)) selectedSkills.add(skill.name);
        else selectedSkills.delete(skill.name);
        selected.checked = selectedSkills.has(skill.name);
        updateControls();
      });
      item.append(title, description, source, enabledLabel, selectedLabel); node('skills-list').append(item);
    }
    node('skills-diagnostics').replaceChildren();
    for (const diagnostic of skillDiagnostics) {
      const item = document.createElement('li'); item.textContent = `${diagnostic.location}: ${diagnostic.code} — ${diagnostic.detail}`;
      node('skills-diagnostics').append(item);
    }
    node('skills-status').textContent = text(skillItems.length ? 'skillsLoaded' : 'skillsEmpty');
    node('skills-edit').disabled = selectedSkills.size !== 1;
    renderSkillSuggestions();
  }

  function pumpSkills() {
    if (!skillRefreshRequested || skillsLoading || !online() || busy || imageLoading || needsCancel || cancelling) return;
    skillRefreshRequested = false; skillsLoading = true;
    void loadSkills().finally(() => { skillsLoading = false; });
  }

  function requestSkills() {
    if (skillsLoading) return;
    skillRefreshRequested = true;
    pumpSkills();
  }

  async function loadSkills() {
    await operate(async () => {
      try {
        const result = await bridge.request('byok.skills.list', {});
        if (disposed) return;
        skillItems = result.skills; skillDiagnostics = result.diagnostics; skillsLoaded = true;
        for (const name of selectedSkills) if (!skillItems.some(skill => skill.name === name && skill.enabled && skill.modes.includes(node('mode').value))) selectedSkills.delete(name);
        renderSkills();
      } catch (error) { node('skills-status').textContent = text(errorLabel(error.code)); throw error; }
    });
  }

  async function setSkillEnabled(name, enabled) {
    await operate(async () => {
      let failure = null;
      try {
        const result = await bridge.request('byok.skills.set', { name, enabled });
        const item = skillItems.find(skill => skill.name === result.name);
        item.enabled = result.enabled;
        if (!item.enabled) selectedSkills.delete(name);
      } catch (error) { failure = error; throw error; }
      finally {
        if (!disposed) {
          renderSkills();
          if (failure) node('skills-status').textContent = text(errorLabel(failure.code));
        }
      }
    });
  }

  async function writeSkill(edit) {
    const selected = edit ? [...selectedSkills][0] : null;
    const item = selected ? skillItems.find(skill => skill.name === selected) : null;
    const name = edit ? item?.name : window.prompt(text('skillsNamePrompt'), 'new-skill');
    if (!name) return;
    const description = window.prompt(text('skillsDescriptionPrompt'), item?.description || 'Describe this skill.');
    if (description === null) return;
    const body = window.prompt(text('skillsBodyPrompt'), 'Instructions for the assistant.');
    if (body === null) return;
    try { await bridge.request('byok.skills.write', { name, description, body, edit }); node('skills-undo').disabled = false; requestSkills(); }
    catch (error) { node('skills-status').textContent = text(errorLabel(error.code)); }
  }
  async function undoSkill() {
    try { await bridge.request('byok.skills.undo', {}); node('skills-undo').disabled = true; requestSkills(); }
    catch (error) { node('skills-status').textContent = text(errorLabel(error.code)); }
  }
  async function openSkillsFolder() {
    try { await bridge.request('byok.skills.open', {}); }
    catch (error) { node('skills-status').textContent = text(errorLabel(error.code)); }
  }

  function renderAgents() {
    const select = node('agents-name'), previous = select.value;
    select.replaceChildren(...agentItems.map(agent => {
      const option = document.createElement('option'); option.value = agent.name; option.textContent = agent.name;
      return option;
    }));
    if (agentItems.some(agent => agent.name === previous)) select.value = previous;
    else if (agentItems.length) select.value = agentItems[0].name;
    node('agents-list').replaceChildren();
    for (const agent of agentItems) {
      const item = document.createElement('li'), title = document.createElement('strong'), description = document.createElement('p'), source = document.createElement('p');
      title.textContent = agent.name; description.className = 'secondary'; description.textContent = agent.description;
      source.className = 'secondary'; source.textContent = `${agent.source} · ${agent.scope}`;
      item.append(title, description, source); node('agents-list').append(item);
    }
    node('agents-diagnostics').replaceChildren();
    for (const diagnostic of agentDiagnostics) {
      const item = document.createElement('li'); item.textContent = `${diagnostic.location}: ${diagnostic.code} — ${diagnostic.detail}`;
      node('agents-diagnostics').append(item);
    }
    node('agents-status').textContent = text(agentItems.length ? 'agentsLoaded' : 'agentsEmpty');
    node('agents-delegate').disabled = !agentItems.length || node('mode').value !== 'Agent';
  }

  function pumpAgents() {
    if (!agentRefreshRequested || agentsLoading || !online() || busy || imageLoading || needsCancel || cancelling) return;
    agentRefreshRequested = false; agentsLoading = true;
    void loadAgents().finally(() => { agentsLoading = false; });
  }

  function requestAgents() {
    if (agentsLoading) return;
    agentRefreshRequested = true; pumpAgents();
  }

  async function loadAgents() {
    await operate(async () => {
      try {
        const result = await bridge.request('byok.agents.list', {});
        if (disposed) return;
        agentItems = result.agents; agentDiagnostics = result.diagnostics; agentsLoaded = true;
        renderAgents();
      } catch (error) { node('agents-status').textContent = text(errorLabel(error.code)); throw error; }
    });
  }

  async function delegateAgent(event) {
    event.preventDefault();
    const name = node('agents-name').value, taskValue = node('agents-task').value;
    if (!name || node('mode').value !== 'Agent') { node('agents-result-status').textContent = text('agentsNeedAgentMode'); return; }
    await operate(async () => {
      try {
        const result = await bridge.request('byok.agents.delegate', { name, task: taskValue, mode: 'Agent' });
        node('agents-result').textContent = result.text; node('agents-result').hidden = false;
        node('agents-result-status').textContent = text('agentsDelegated');
      } catch (error) { node('agents-result-status').textContent = text(errorLabel(error.code)); throw error; }
    });
  }

  function renderDiagnostics() {
    node('diag-level').value = diagLevel;
    node('diag-level').disabled = !diagLoaded || diagBusy;
    node('diag-open').disabled = !diagLoaded || diagBusy;
  }

  async function loadDiagnostics() {
    if (diagBusy || !online()) return;
    diagBusy = true; renderDiagnostics();
    try {
      const result = await bridge.request('byok.log.get', {});
      diagLevel = result.level; diagLoaded = true;
      node('diag-status').textContent = '';
    } catch (error) { node('diag-status').textContent = text(errorLabel(error.code)); }
    finally { diagBusy = false; renderDiagnostics(); }
  }

  async function changeDiagnosticLevel() {
    if (diagBusy || !diagLoaded) return;
    const level = node('diag-level').value;
    diagBusy = true; renderDiagnostics();
    try {
      const result = await bridge.request('byok.log.set', { level });
      diagLevel = result.level; node('diag-status').textContent = text('diagSaved');
    } catch (error) { node('diag-status').textContent = text(errorLabel(error.code)); }
    finally { diagBusy = false; renderDiagnostics(); }
  }

  async function openDiagnosticLog() {
    if (diagBusy || !diagLoaded) return;
    diagBusy = true; renderDiagnostics();
    try {
      await bridge.request('byok.log.open', {});
      node('diag-status').textContent = text('diagOpened');
    } catch (error) { node('diag-status').textContent = text(errorLabel(error.code)); }
    finally { diagBusy = false; renderDiagnostics(); }
  }

  function applyCatalog(result) {
    models = result.models; selectedModelId = ''; clearProbeConsent();
    // Public catalogs can succeed without verifying a key; errors with unknown validity must not be saved.
    const catalogUnverified = result.credentialValid === null && !result.code;
    // A 429 / 402 may validate credentials despite returning no models. Never relabel it as a 401.
    if (result.credentialValid === true || catalogUnverified) {
      configurations = [...configurations.filter(config => config.id !== result.configuration.id), result.configuration];
      configurationId = result.configuration.id;
    }
    if (configurations.some(config => config.id === result.configuration.id)) {
      configurationId = result.configuration.id;
      selectedModelId = models.some(model => model.id === result.configuration.selectedModelId) ? result.configuration.selectedModelId : '';
    }
    renderProviders(); renderModels(); renderCapabilities();
    setStatus(result.code ? result.credentialValid === true ? 'byokCredentialValid' : 'byokReady'
      : catalogUnverified ? 'byokCatalogUnverified' : selectedModelId ? 'byokSelected' : 'byokCatalogReady');
    if (result.code) showError(result.code, result.retryAfter);
  }
  async function listProviders() {
    await operate(async () => {
      const result = await bridge.request('byok.list', {});
      if (disposed) return;
      configurations = result.configurations; remainingBudget = result.remainingBudget; renderProbeBudget();
      loaded = true; configurationId = ''; resetSelection(); cancelRemoveProvider(false); renderProviders();
      setStatus(configurations.length ? 'byokReady' : 'byokEmptyProviders');
    });
  }
  async function discover() {
    if (!configurationId) return;
    removeAttachment();
    const id = configurationId; resetSelection();
    await operate(async () => {
      const result = await bridge.request('byok.discover', { configurationId: id });
      if (!disposed && configurationId === id) applyCatalog(result);
    }, node('models'));
  }
  function requestRemoveProvider() {
    if (node('remove').disabled || !configurationId) return;
    removeTarget = configurationId;
    node('remove-confirmation').hidden = false;
    node('remove-keep').focus();
  }
  function cancelRemoveProvider(focus = true) {
    if (!removeTarget && node('remove-confirmation').hidden) return;
    const hadTarget = Boolean(removeTarget);
    removeTarget = '';
    node('remove-confirmation').hidden = true;
    if (focus && hadTarget) node('remove').focus({ preventScroll: true });
  }
  async function deleteProvider() {
    if (!removeTarget || node('remove-confirmation').hidden) return;
    const id = removeTarget;
    await operate(async () => {
      await bridge.request('byok.remove', { configurationId: id });
      if (disposed) return;
      configurations = configurations.filter(config => config.id !== id);
      cancelRemoveProvider(false);
      const removedTabIds = new Set(savedConversations.filter(item => item.configurationId === id).map(item => item.id));
      openTabs = openTabs.filter(item => !item.id || !removedTabIds.has(item.id));
      if (configurationId === id) {
        configurationId = ''; resetSelection();
        if (activeConversation?.configurationId === id) {
          resetHistory();
          if (!openTabs.some(item => item.id === null)) openTabs.push({ id: null, title: '', modelId: '', turnCount: 0 });
        }
      }
      renderProviders(); renderTabs(); persistTabs(); syncEmpty();
      setStatus(configurations.length ? 'byokReady' : 'byokEmptyProviders');
    }, node('providers'));
  }
  async function requestAzureInventory() {
    if (!configurationId || node('kind').value !== 'Azure') return;
    const subscriptionId = node('azure-subscription').value.trim();
    const resourceGroup = node('azure-resource-group').value.trim();
    const accountName = node('azure-account').value.trim();
    const tenantId = node('azure-tenant').value.trim();
    const clientId = node('azure-client').value.trim();
    if (!subscriptionId || !resourceGroup || !accountName || !tenantId || !clientId) {
      node('azure-inventory-status').textContent = text('byokAzureDeviceCode');
      return;
    }
    const btn = node('azure-inventory');
    btn.disabled = true;
    node('azure-inventory-status').textContent = '';
    try {
      const result = await bridge.request('byok.azure.arm.list', {
        configurationId, tenantId, clientId, subscriptionId, resourceGroup, accountName
      });
      if (disposed) return;
      const deployments = result.deployments || [];
      if (deployments.length === 0) {
        node('azure-inventory-status').textContent = text('byokEmptyProviders');
        return;
      }
      node('deployments').value = deployments.join('\n');
      node('azure-inventory-status').textContent = `${deployments.length} deployment(s) found`;
      // Also update the configuration with ARM details
      configurations = configurations.map(config => config.id === configurationId ? { ...config, arm: result.arm } : config);
    } catch (error) {
      if (!disposed) node('azure-inventory-status').textContent = error.message || text('byokErrorUrl');
    } finally {
      if (!disposed) btn.disabled = false;
    }
  }
  function changeKind() {
    const kind = node('kind').value;
    node('url').value = DEFAULT_BASE_URLS[kind]; node('url').placeholder = kind === 'Azure' ? text('byokAzureUrlPlaceholder') : '';
    node('provider-id').value = kind.toLowerCase(); node('key').value = '';
    node('deployments-group').hidden = kind !== 'Azure';
    node('key-label').textContent = text(kind === 'Ollama' || kind === 'LMStudio' ? 'byokKeyLocal' : 'byokKey');
    if (kind !== 'Azure') {
      ['azure-subscription', 'azure-resource-group', 'azure-account', 'azure-tenant', 'azure-client']
        .forEach(id => node(id).value = '');
      node('azure-inventory-status').textContent = '';
    }
    updateControls();
  }
  async function configure(event) {
    event.preventDefault();
    let key = node('key').value;
    node('key').value = '';
    if (!online() || busy || imageLoading || needsCancel || cancelling) { key = ''; return; }
    await operate(async () => {
      const id = crypto.randomUUID();
      try {
        const baseUrl = normalizeBaseUrl(node('url').value);
        const names = node('kind').value === 'Azure' ? node('deployments').value.split(/\r?\n/).map(name => name.trim()).filter(Boolean) : [];
        const arm = node('kind').value === 'Azure' && node('azure-subscription').value.trim() ? {
          subscriptionId: node('azure-subscription').value.trim(),
          resourceGroup: node('azure-resource-group').value.trim(),
          accountName: node('azure-account').value.trim()
        } : null;
        const request = bridge.request('byok.configure', {
          id, providerId: node('provider-id').value.trim(), kind: node('kind').value, baseUrl, key,
          ...(names.length ? { deploymentNames: names } : {}),
          ...(arm ? { arm } : {})
        });
        key = '';
        const result = await request;
        if (disposed) return;
        configurationId = ''; resetSelection(); applyCatalog(result);
      } finally { key = ''; }
    }, node('models'));
  }
  function modelChangeBlocked(model) {
    // A model switch replays the whole session; the new model must support every capability the stored turns already use.
    if (!model || !activeConversation) return false;
    const usedBlocks = records.filter(item => item && item.blocks).flatMap(item => item.blocks);
    if (usedBlocks.some(block => block.type === 'tool') && !model.tools) return 'byokErrorTools';
    if (activeConversation.hasImage && !model.vision) return 'vision_not_supported';
    return false;
  }
  async function chooseModel() {
    const modelId = node('models').value;
    const next = models.find(model => model.id === modelId);
    const blocked = modelChangeBlocked(next);
    if (blocked) {
      showError(blocked);
      node('models').value = selectedModelId;
      renderModels();
      return;
    }
    selectedModelId = ''; clearProbeConsent(); removeAttachment(); renderCapabilities();
    if (!modelId) return;
    await operate(async () => {
      const result = await bridge.request('byok.select', { configurationId, modelId });
      if (disposed) return;
      configurations = configurations.map(config => config.id === result.configuration.id ? result.configuration : config);
      selectedModelId = result.configuration.selectedModelId; renderModels(); renderCapabilities(); setStatus('byokSelected');
      const tab = openTabs.find(item => item.id === activeConversation?.id)
        || (!activeConversation ? openTabs.find(item => item.id === null) : null);
      if (tab) { tab.modelId = selectedModelId; renderTabs(); persistTabs(); }
      syncEmpty();
    }, node('message'));
  }
  async function override() {
    if (node('apply-override').disabled) return;
    const modelId = selectedModelId;
    const displayName = node('display-name').value.trim();
    const efforts = [...node('effort-levels').querySelectorAll('input:checked')].map(input => input.value);
    const enabled = node('model-enabled').checked;
    await operate(async () => {
      const result = await bridge.request('byok.override', {
        configurationId, modelId,
        ...Object.fromEntries(Object.keys(capabilityNames).map(key => [key, node(key).value === 'true'])),
        ...(displayName ? { displayName } : {}),
        ...(efforts.length ? { efforts } : {}),
        enabled
      });
      if (disposed) return;
      models = Object.hasOwn(result, "models") ? result.models : models.map(model => model.id === result.model.id ? result.model : model);
      removeAttachment(); renderModels(); renderCapabilities(); setStatus('byokSelected');
    });
  }
  async function probe() {
    if (!canProbe()) return;
    const id = configurationId, modelId = selectedModelId, capability = node('probe-capability').value;
    probing = true;
    try {
      await operate(async () => {
        try {
          // Model identity is correlation-only: the native host derives the selected model, never a wire model ID.
          const result = await bridge.request('byok.probe', { configurationId: id, capability, consent: true }, { expectedModelId: modelId });
          if (disposed) return;
          if (configurationId !== id || selectedModelId !== modelId || result.model.id !== selectedModelId) throw new ByokError('invalid_response');
          remainingBudget = result.remainingBudget; renderProbeBudget();
          if (result.supported === null) { setStatus('byokProbeInconclusive'); return; }
          // Keep the native evidence precedence; a probe must not replace a stronger origin in the browser.
          models = models.map(model => model.id === modelId ? result.model : model);
          if (!selectedModel().vision) removeAttachment();
          renderModels(); renderCapabilities(); setStatus(result.supported ? 'byokProbeSupported' : 'byokProbeUnsupported');
        } catch (error) {
          if (!disposed) {
            // Failed/cancelled probes may have spent tokens. Only the native session can reconcile the budget.
            remainingBudget = null; clearProbeConsent(); renderProbeBudget();
            if (cancelRequest) { try { await cancelRequest; } catch { /* Budget reconciliation still uses a read-only RPC. */ } }
            if (online()) {
              try {
                const result = await bridge.request('byok.list', {});
                if (!disposed) { remainingBudget = result.remainingBudget; renderProbeBudget(); }
              } catch { /* Unknown budget keeps further paid probes disabled until a successful list. */ }
            }
          }
          throw error;
        }
      }, node('probe-budget'), 'byokProbing');
    } finally {
      probing = false; clearProbeConsent(); if (!disposed) updateControls();
    }
  }
  async function processImageFile(file) {
    if (!file) { removeAttachment(); updateControls(); return; }
    if (!selectedModel()?.vision) {
      showError('vision_not_supported');
      const announcement = document.getElementById('announcement');
      if (announcement) announcement.textContent = text('byokErrorVision');
      updateControls();
      return;
    }
    if (!online() || busy || needsCancel) { updateControls(); return; }
    removeAttachment();
    const generation = attachmentGeneration;
    imageLoading = true; clearError(); updateControls();
    try {
      const result = await readImageAttachment(file);
      if (disposed || generation !== attachmentGeneration || !online() || !selectedModel()?.vision) return;
      attachment = result; node('attachment').textContent = text('byokImageAttached'); node('attachment').hidden = false; node('remove-image').hidden = false;
      const announcement = document.getElementById('announcement');
      if (announcement) announcement.textContent = text('byokImageAttached');
    } catch (error) {
      if (!disposed && generation === attachmentGeneration) {
        showError(error.code || 'invalid_image_type');
        const announcement = document.getElementById('announcement');
        if (announcement) announcement.textContent = text(errorLabel(error.code || 'invalid_image_type'));
      }
    } finally { imageLoading = false; clearProbeConsent(); if (!disposed) updateControls(); }
  }

  function createTurn(value, contextText) {
    const article = document.createElement('article'); article.className = 'byok-turn';
    const user = localized('h3', 'byokUser'); const userText = document.createElement('p'); userText.className = 'byok-plain'; userText.textContent = value;
    const assistant = localized('h3', 'byokAssistant');
    const content = document.createElement('div'); content.className = 'byok-blocks';
    const usage = document.createElement('p'); usage.className = 'secondary';
    const notices = document.createElement('div');
    article.append(user, userText);
    if (contextText) {
      const details = document.createElement('details'); details.className = 'context-history';
      const body = document.createElement('pre'); body.className = 'byok-plain'; body.tabIndex = 0; body.textContent = contextText;
      details.append(localized('summary', 'contextHistory'), body); article.append(details);
    }
    article.append(assistant, content, usage, notices);
    return { article, content, usage, notices, calls: new Map(), approvals: new Map(), blocks: [], record: { userText: value,
      ...(contextText ? { contextText } : {}), hasImage: false, blocks: [], steps: [], status: 'failed' } };
  }
  // A turn is an ordered timeline: every reasoning/text/tool step becomes its own block appended
  // in arrival order, so think -> tool -> think -> answer renders exactly as it happened.
  function appendBlock(turn, type, data = {}) {
    data.type = type;
    const block = { type, data };
    if (type === 'reasoning' || type === 'text') {
      data.text ??= '';
      const body = document.createElement(type === 'reasoning' ? 'p' : 'div'); body.className = 'byok-plain';
      if (type === 'reasoning') {
        const details = document.createElement('details'); details.className = 'byok-reasoning';
        details.append(localized('summary', 'byokReasoning'), body);
        block.el = details;
      } else block.el = body;
      block.body = body;
    } else if (type === 'tool') {
      const card = document.createElement('details'); card.className = 'byok-tool'; card.open = true; card.dataset.timestamp = data.timestamp ?? '';
      const title = localized('summary', 'byokToolNotice'); const name = document.createElement('p'); name.textContent = data.name ?? '';
      const state = localized('p', 'byokToolPending', 'secondary');
      state.setAttribute('role', 'status'); state.setAttribute('aria-live', 'polite'); state.setAttribute('aria-atomic', 'true');
      const args = document.createElement('pre'); args.tabIndex = 0;
      args.className = 'byok-plain'; args.dataset.i18nLabel = 'byokToolArguments'; args.setAttribute('aria-label', text('byokToolArguments'));
      const result = document.createElement('pre'); result.className = 'byok-plain byok-tool-result'; result.tabIndex = 0; result.hidden = true;
      result.dataset.i18nLabel = 'byokToolResult'; result.setAttribute('aria-label', text('byokToolResult'));
      card.append(title, name, state, args, result);
      block.el = card; block.title = title; block.state = state; block.args = args; block.result = result;
    } else if (type === 'approval') {
      const card = document.createElement('section'); card.className = 'byok-approval';
      const name = document.createElement('p'); name.className = 'byok-plain'; name.textContent = data.toolName;
      card.append(localized('h4', 'approvalTitle'), name);
      if (data.targetPath !== null) {
        const target = document.createElement('p'); target.className = 'byok-plain';
        target.textContent = `${text('approvalTarget')}: ${data.targetPath}`; card.append(target);
      }
      const args = document.createElement('pre'); args.className = 'byok-plain'; args.tabIndex = 0;
      args.dataset.i18nLabel = 'approvalArguments'; args.setAttribute('aria-label', text('approvalArguments'));
      // Model-supplied arguments stay inert text, never Markdown.
      args.textContent = JSON.stringify(data.arguments, null, 2);
      const state = localized('p', 'approvalPending', 'secondary');
      state.setAttribute('role', 'status'); state.setAttribute('aria-live', 'polite'); state.setAttribute('aria-atomic', 'true');
      const actions = document.createElement('div'); actions.className = 'byok-actions';
      const buttons = [], entry = { answered: false };
      const settle = key => {
        entry.answered = true; for (const button of buttons) button.disabled = true;
        state.dataset.byokText = key; state.textContent = text(key);
        const announcement = document.getElementById('announcement');
        if (announcement) announcement.textContent = state.textContent;
      };
      entry.expire = () => settle('approvalExpired');
      const choices = [['deny', 'approvalDeny'], ['allow_once', 'approvalAllowOnce'],
        ...(data.canAllowSession ? [['allow_session', 'approvalAllowSession']] : [])];
      for (const [decision, key] of choices) {
        const button = localized('button', key, decision === 'deny' ? 'primary-button' : 'text-button'); button.type = 'button';
        button.addEventListener('click', async () => {
          if (entry.answered || disposed) return;
          for (const item of buttons) item.disabled = true;
          try {
            await bridge.request('byok.approval.respond', { requestId: data.id, decision });
            settle(decision === 'deny' ? 'approvalDenied' : decision === 'allow_once' ? 'approvalAllowedOnce' : 'approvalAllowedSession');
          } catch { if (!disposed) settle('approvalExpired'); }
        });
        buttons.push(button); actions.append(button);
      }
      card.append(args, state, actions);
      block.el = card; block.state = state;
      turn.approvals.set(data.id, entry);
      const announcement = document.getElementById('announcement');
      if (announcement) announcement.textContent = `${text('approvalTitle')}: ${data.toolName}`;
      buttons[0].focus({ preventScroll: true });
    }
    turn.blocks.push(block);
    if (type !== 'approval') turn.record.blocks.push(block.data);
    turn.content.append(block.el);
    return block;
  }
  function renderEvent(turn, event) {
    if (disposed) return;
    const pinned = !turn.archived && scrollPinned();
    const timestamp = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const step = { kind: event.kind, timestamp, ...event };
    turn.record.steps.push(step);
    if (event.kind === 'TextDelta') {
      const last = turn.blocks.at(-1);
      const block = last?.type === 'text' ? last : appendBlock(turn, 'text');
      // Chromium Text nodes have no dataset; a span keeps the chunk inert and timestamped.
      const chunk = document.createElement('span');
      chunk.textContent = event.text;
      chunk.dataset.timestamp = timestamp;
      block.body.append(chunk);
      block.data.text += event.text;
    } else if (event.kind === 'ReasoningDelta') {
      const last = turn.blocks.at(-1);
      const block = last?.type === 'reasoning' ? last : appendBlock(turn, 'reasoning');
      const chunk = document.createElement('span');
      chunk.textContent = event.text;
      chunk.dataset.timestamp = timestamp;
      block.body.append(chunk);
      block.data.text += event.text;
    } else if (event.kind === 'ToolCallStart') {
      const block = appendBlock(turn, 'tool', { id: event.id, name: event.name });
      turn.calls.set(event.id, { block, args: block.args, state: block.state, title: block.title, result: block.result, name: event.name });
    } else if (event.kind === 'ToolCallArgsDelta') turn.calls.get(event.id).args.append(document.createTextNode(event.chunk));
    else if (event.kind === 'ToolCallEnd') {
      const call = turn.calls.get(event.id); call.args.textContent = JSON.stringify(event.arguments, null, 2); call.args.dataset.timestamp = timestamp;
      call.state.dataset.byokText = 'byokToolEnded'; call.state.textContent = text('byokToolEnded');
      call.block.data.arguments = event.arguments;
    } else if (event.kind === 'ToolExecution') {
      const call = turn.calls.get(event.id);
      if (event.status === 'Running' || event.status === 'Completed') {
        call.title.dataset.byokText = 'byokToolExecutedNotice'; call.title.textContent = text('byokToolExecutedNotice');
      }
      call.state.dataset.byokText = `byokTool${event.status}`; call.state.textContent = text(`byokTool${event.status}`);
      call.state.className = event.isError ? 'error' : 'secondary';
      call.result.textContent = event.content; call.result.hidden = event.status === 'Running'; call.result.dataset.timestamp = timestamp;
      if (event.status !== 'Running') {
        Object.assign(call.block.data, { status: event.status, isError: event.isError, content: event.content });
      }
    } else if (event.kind === 'ApprovalRequest') {
      appendBlock(turn, 'approval', event);
    } else if (event.kind === 'Usage') {
      turn.usage.replaceChildren(localized('span', 'byokUsage'), document.createTextNode(`: ${event.input} / ${event.output}`));
    } else if (event.kind === 'Notice') {
      if (event.code === 'tool_budget_paused') {
        const line = localized('p', 'byokNoticePaused', 'secondary');
        const resume = localized('button', 'byokContinue', 'text-button'); resume.type = 'button';
        resume.addEventListener('click', () => continuePaused(turn));
        turn.notices.append(line, resume);
        const announcement = document.getElementById('announcement');
        if (announcement) announcement.textContent = line.textContent;
      } else {
        const line = localized('p', event.code === 'history_compacted' ? 'byokNoticeCompacted' : 'byokNoticeRetrying', 'secondary');
        turn.notices.append(line);
        const announcement = document.getElementById('announcement');
        if (announcement) announcement.textContent = line.textContent;
      }
    } else if (event.kind === 'Error') showError(event.code, event.retryAfter);
    if (pinned) node('output').scrollTop = node('output').scrollHeight;
  }
  function finishMarkdown(turn) {
    if (typeof renderMarkdown !== 'function') return;
    // Only the main's sanitized renderer may return HTML; streaming is always text nodes.
    try {
      for (const block of turn.blocks) {
        if (block.type !== 'text' || !block.data.text) continue;
        const html = renderMarkdown(block.data.text);
        if (typeof html !== 'string') continue;
        block.el.innerHTML = html; block.el.className = 'markdown';
        for (const pre of block.el.querySelectorAll('pre')) {
          pre.tabIndex = 0; pre.setAttribute('aria-label', text('byokCodeBlock'));
          const wrapper = document.createElement('div'); wrapper.className = 'code-block';
          const copy = localized('button', 'byokCopy', 'code-copy'); copy.type = 'button';
          pre.replaceWith(wrapper); wrapper.append(pre, copy);
          copy.addEventListener('click', async () => {
            const value = (pre.querySelector('code') || pre).textContent;
            let copied = false;
            try { await navigator.clipboard.writeText(value); copied = true; }
            catch {
              const scratch = document.createElement('textarea'); scratch.value = value; scratch.className = 'sr-only'; scratch.setAttribute('readonly', '');
              document.body.append(scratch); scratch.select();
              try { copied = document.execCommand('copy'); } finally { scratch.remove(); }
            }
            copy.textContent = text(copied ? 'byokCopied' : 'byokCopyFailed');
            setTimeout(() => { copy.textContent = text('byokCopy'); }, 2000);
          });
        }
      }
    } catch { /* A renderer failure must leave already received plain text intact. */ }
  }
  function renderArchived(record) {
    if (!record) return localized('article', 'historyLoading', 'history-placeholder');
    const turn = createTurn(record.userText, record.contextText); turn.archived = true;
    if (record.hasImage) turn.article.append(localized('p', 'byokImageAttached', 'secondary'));
    for (const block of archivedBlocks(record)) renderBlock(turn, block);
    if (record.status === 'paused') {
      turn.article.append(localized('p', 'historyPausedTurn', 'secondary'));
      if (record.consents) {
        const resume = localized('button', 'byokContinue', 'text-button'); resume.type = 'button';
        resume.addEventListener('click', () => continuePaused(record));
        turn.article.append(resume);
      }
    } else if (record.status !== 'completed') turn.article.append(localized('p', record.status === 'cancelled' ? 'historyCancelledTurn' : 'historyFailedTurn', 'secondary'));
    finishMarkdown(turn); return turn.article;
  }
  function archivedBlocks(record) {
    if (Array.isArray(record.blocks) && record.blocks.length) return record.blocks;
    // Flattened records (history saved before ordered blocks) keep their grouped order.
    const blocks = [];
    if (record.reasoning) blocks.push({ type: 'reasoning', text: record.reasoning });
    if (record.text) blocks.push({ type: 'text', text: record.text });
    for (const call of record.tools ?? []) blocks.push({ type: 'tool', ...call });
    return blocks;
  }
  function renderBlock(turn, block) {
    if (block.type === 'reasoning' || block.type === 'text') {
      const created = appendBlock(turn, block.type, { text: block.text ?? '' });
      created.body.textContent = block.text ?? '';
    } else if (block.type === 'tool') {
      const created = appendBlock(turn, 'tool', { id: block.id, name: block.name });
      created.args.textContent = JSON.stringify(block.arguments, null, 2);
      if (block.status) {
        if (block.status === 'Running' || block.status === 'Completed') {
          created.title.dataset.byokText = 'byokToolExecutedNotice'; created.title.textContent = text('byokToolExecutedNotice');
        }
        created.state.dataset.byokText = `byokTool${block.status}`; created.state.textContent = text(`byokTool${block.status}`);
        created.state.className = block.isError ? 'error' : 'secondary';
        created.result.textContent = block.content ?? ''; created.result.hidden = block.status === 'Running';
      } else {
        created.state.dataset.byokText = 'byokToolEnded'; created.state.textContent = text('byokToolEnded');
      }
    }
  }
  function continuePaused(source) {
    const consents = source?.consents;
    if (!consents || busy || !online() || !selectedModel() || !configurationId || node('message').value.trim()) return;
    // The click is the explicit consent to resume with the same mode and permission level.
    if (['Ask', 'Plan', 'Agent'].includes(consents.mode)) node('mode').value = consents.mode;
    if (['Interactive', 'Permissive', 'FullAccess'].includes(consents.permission)) node('permission').value = consents.permission;
    modeChanged();
    node('message').value = text('byokContinueMessage');
    updateControls();
    if (!node('send').disabled) node('chat-form').requestSubmit();
  }
  async function submitChat(event) {
    event.preventDefault();
    if (node('send').disabled || !online() || busy || imageLoading || needsCancel || cancelling || !selectedModel() || !configurationId
      || !contextWithinBounds() || !node('message').value.trim() || node('message').value.length > MAX_PROMPT_LENGTH
      || (activeConversation && activeConversation.configurationId !== configurationId)) return;
    const permission = node('permission').value;
    const value = node('message').value;
    const mentions = parseMentions(value);
    busy = true; clearError(); setStatus(mentions.length ? 'byokMentions' : 'byokStreaming'); updateControls();
    let turn = null, failedMention = null;
    try {
      for (const mention of mentions) {
        const normalized = mention.kind === 'file' ? mention.path.replace(/^\.\//, '').toLowerCase() : null;
        if (contexts.some(item => mention.kind === 'solution' ? item.kind === 'solution' : item.kind === 'file' && item.label.toLowerCase() === normalized)) continue;
        if (contexts.length >= MAX_CONTEXTS || contextSize() >= MAX_CONTEXT_TOTAL) { failedMention = mention.token; throw new ByokError('context_too_large'); }
        let captured;
        try { captured = await bridge.request('byok.context.capture', { kind: mention.kind, ...(mention.kind === 'file' ? { path: mention.path } : {}) }); }
        catch (error) { failedMention = mention.token; throw error; }
        if (disposed) return;
        contexts.push(captured.context); renderContexts();
        if (!contextWithinBounds()) { failedMention = mention.token; throw new ByokError('context_too_large'); }
      }
      streaming = true; setStatus('byokStreaming'); updateControls();
      const contextText = contexts.map(item => `[Context: ${item.label}]\n${item.text}`).join('\n\n');
      turn = createTurn(value, contextText); turn.record.hasImage = Boolean(attachment);
      turn.consents = { mode: node('mode').value, permission };
      liveTurn = turn; node('live').replaceChildren(turn.article); syncEmpty();
      node('output').scrollTop = node('output').scrollHeight;
      const request = bridge.request('byok.chat', { configurationId, text: value, mode: node('mode').value, permission,
        ...(selectedSkills.size ? { skills: [...selectedSkills] } : {}),
        effort: selectedModel()?.reasoning && selectedModel().efforts.length ? node('effort').value : 'Off',
        ...(attachment ? { image: attachment } : {}),
        ...(contexts.length ? { contextIds: contexts.map(item => item.id) } : {})
      }, { onEvent: event => renderEvent(turn, event) });
      removeAttachment();
      const result = await request;
      if (disposed) return;
      contexts = []; renderContexts();
      turn.record.status = result.completed ? 'completed' : result.paused ? 'paused' : 'failed';
      turn.record.consents = turn.consents;
      rememberSummary(result.conversation);
      records.length = result.conversation.turnCount;
      cacheTurns([{ ...turn.record, index: result.conversation.turnCount - 1 }]);
      liveTurn = null; node('live').replaceChildren(); history.items = records; history.invalidate(); history.jump(true);
      if (result.completed) { node('message').value = ''; selectedSkills.clear(); renderSkills(); setStatus('byokCompleted'); }
      else if (result.paused) { node('message').value = ''; setStatus('byokPaused'); }
      else if (!errorCode) showError('provider_error');
    } catch (error) {
      if (!disposed) {
        showError(error.code);
        if (failedMention) node('error').textContent = text('byokMentionFailed', { mention: `@${failedMention}`, reason: text(errorLabel(error.code)) });
        if (turn) {
          historyLoaded = false;
          turn.article.append(localized('p', error.code === 'cancelled' ? 'historyCancelledTurn' : 'historyInterruptedTurn', 'secondary'));
        }
        if (error.code === 'timeout' || error.code === 'invalid_response' || error.code === 'stream_too_large') needsCancel = true;
      }
    } finally {
      busy = false; streaming = false; if (!cancelling) cancelRequested = false; clearProbeConsent();
      // The host revokes unanswered approvals when the chat ends; the card must never stay actionable.
      if (turn) for (const approval of turn.approvals.values()) if (!approval.answered) approval.expire();
      if (turn) requestWriteStatus();
      if (!disposed) { if (turn && liveTurn === turn) finishMarkdown(turn); updateControls(); queueMicrotask(pumpHistory); if (!node('message').disabled) node('message').focus({ preventScroll: true }); }
    }
  }
  async function cancel() {
    if (!online() || cancelling || cancelRequested || (!busy && !needsCancel)) return;
    const recovering = needsCancel && !busy;
    cancelling = true; cancelRequested = true; clearProbeConsent(); updateControls();
    try {
      cancelRequest = bridge.request('byok.cancel', {});
      await cancelRequest;
      if (!disposed) {
        needsCancel = false;
        // An acknowledgement is not the operation's outcome; a completed request may have won the race.
        if (recovering) {
          clearError(); setStatus('byokCancelled');
          if (readErrorCode) { readErrorCode = null; readStatusKey = 'byokCancelled'; renderReadStatus(); }
          if (writeErrorCode) { writeErrorCode = null; writeStatusKey = 'byokCancelled'; renderWriteStatus(); }
        }
      }
    } catch (error) { cancelRequested = false; if (!disposed) showError(error.code); }
    finally {
      cancelRequest = null; cancelling = false; if (!busy) cancelRequested = false; clearProbeConsent();
      if (!disposed) {
        updateControls();
        if (!busy && panel.open) (errorCode ? node('error') : node('status')).focus({ preventScroll: true });
      }
    }
  }
  function refreshLocale() {
    if (disposed) return;
    for (const root of [panel, node('history'), node('read-tools'), node('write-tools'), node('skills'), node('chat-form'), node('output')]) {
      for (const target of root.querySelectorAll('[data-i18n]')) target.textContent = text(target.dataset.i18n);
      for (const target of root.querySelectorAll('[data-i18n-label]')) target.setAttribute('aria-label', text(target.dataset.i18nLabel));
      for (const target of root.querySelectorAll('[data-i18n-placeholder]')) target.setAttribute('placeholder', text(target.dataset.i18nPlaceholder));
      for (const target of root.querySelectorAll('[data-byok-text]')) target.textContent = text(target.dataset.byokText);
    }
    node('empty').textContent = text('byokEmpty');
    node('output').setAttribute('aria-label', text('byokChatTitle'));
    node('key-label').textContent = text(['Ollama', 'LMStudio'].includes(node('kind').value) ? 'byokKeyLocal' : 'byokKey');
    if (node('kind').value === 'Azure') node('url').placeholder = text('byokAzureUrlPlaceholder');
    if (attachment) node('attachment').textContent = text('byokImageAttached');
    renderProviders(); renderModels(); renderCapabilities(false); renderProbeBudget(); setStatus(statusKey);
    for (const id of ['history-new', 'history-first', 'history-last', 'error-action', 'history-error-action']) {
      const el = node(id);
      if (el && el.dataset.i18n) el.textContent = text(el.dataset.i18n);
    }
    renderSaved(); renderHistoryStatus(); renderContexts(); renderReadStatus(); renderWriteStatus(); renderSkills();
    for (const name of WRITE_TOOL_NAMES) {
      const target = [...node('write-name').children].find(item => item.value === name);
      if (target) target.textContent = text(name === 'create_file' ? 'writeToolsCreate' : 'writeToolsEdit');
    }
    if (errorCode) showError(errorCode); updateControls();
  }
  function renderTabs() {
    const host = node('session-tabs');
    if (!host) return;
    host.replaceChildren();
    for (const tab of openTabs) {
      const row = document.createElement('div'); row.className = 'session-tab'; row.setAttribute('role', 'presentation');
      const main = document.createElement('button'); main.type = 'button'; main.className = 'session-tab-main';
      main.setAttribute('role', 'tab');
      main.setAttribute('aria-selected', String(tab.id ? tab.id === activeConversation?.id : !activeConversation));
      const title = document.createElement('span'); title.className = 'session-tab-title';
      title.textContent = tab.title || text('sessionDraft');
      const model = document.createElement('span'); model.className = 'session-tab-model';
      const info = models.find(item => item.id === tab.modelId);
      model.textContent = info?.name || tab.modelId || '';
      main.append(title, model);
      main.addEventListener('click', () => void activateTab(tab));
      const close = document.createElement('button'); close.type = 'button'; close.className = 'session-tab-close';
      close.tabIndex = 0; close.textContent = '×';
      close.setAttribute('aria-label', text('sessionCloseLabel', { title: tab.title || text('sessionDraft') }));
      close.addEventListener('click', event => { event.stopPropagation(); void closeTab(tab); });
      row.append(main, close); host.append(row);
    }
  }
  function readStoredTabs() {
    try {
      if (typeof localStorage === 'undefined') return [];
      const raw = localStorage.getItem(TAB_STORAGE_KEY);
      if (!raw) return [];
      const parsed = JSON.parse(raw);
      if (!Array.isArray(parsed)) return [];
      return parsed.slice(0, 32).map(tab => ({
        id: typeof tab?.id === 'string' && tab.id ? tab.id : null,
        title: typeof tab?.title === 'string' ? tab.title : '',
        modelId: typeof tab?.modelId === 'string' ? tab.modelId : '',
        turnCount: Number.isInteger(tab?.turnCount) && tab.turnCount >= 0 ? tab.turnCount : 0
      })).filter(tab => tab.id || tab.title === '');
    } catch { return []; }
  }
  function persistTabs() {
    try {
      if (typeof localStorage === 'undefined') return;
      localStorage.setItem(TAB_STORAGE_KEY, JSON.stringify(openTabs.map(tab => ({
        id: tab.id, title: tab.title, modelId: tab.modelId, turnCount: tab.turnCount
      }))));
    } catch { /* tab persistence is best-effort */ }
  }  function setConnected(value = connected()) {
    if (disposed) return;
    const wasConnected = connectedState;
    connectedState = Boolean(value) && Boolean(connected()) && bridge.available;
    if (!connectedState) {
      bridge.disconnect(); node('key').value = ''; loaded = false; configurationId = ''; configurations = []; resetSelection(); cancelRemoveProvider(false); renderProviders();
      needsCancel = false; remainingBudget = null; renderProbeBudget(); setStatus('byokDisconnected');
      skillItems = []; skillDiagnostics = []; skillsLoaded = false; skillRefreshRequested = false; selectedSkills.clear(); renderSkills();
      agentItems = []; agentDiagnostics = []; agentsLoaded = false; agentRefreshRequested = false; renderAgents();
      diagLoaded = false; renderDiagnostics();
      writePending = null; writeRefreshRequested = false; clearWriteDiff(); renderWriteStatus();
    } else {
      setStatus('byokReady'); if (panel.open && !loaded && !busy) void listProviders();
      if (!wasConnected && node('write-tools').open) requestWriteStatus();
      if (!wasConnected && node('skills').open && !skillsLoaded) requestSkills();
      if (!wasConnected && node('agents').open && !agentsLoaded) requestAgents();
    }
    updateControls();
  }

  const history = new VirtualHistory(node('output'), node('history-spacer'), node('history-items'), renderArchived, range => {
    visible = range; queueMicrotask(pumpHistory);
  });
  listen(node('skills'), 'toggle', () => { if (node('skills').open && online() && !skillsLoaded) requestSkills(); });
  listen(node('skills-reload'), 'click', requestSkills);
  listen(node('skills-create'), 'click', () => void writeSkill(false));
  listen(node('skills-edit'), 'click', () => void writeSkill(true));
  listen(node('skills-open'), 'click', () => void openSkillsFolder());
  listen(node('skills-undo'), 'click', () => void undoSkill());
  listen(node('agents'), 'toggle', () => { if (node('agents').open && online() && !agentsLoaded) requestAgents(); });
  listen(node('agents-reload'), 'click', requestAgents);
  listen(node('agents-form'), 'submit', delegateAgent);
  listen(node('diag-level'), 'change', () => void changeDiagnosticLevel());
  listen(node('diag-open'), 'click', () => void openDiagnosticLog());
  const themePanel = document.getElementById('theme-panel');
  listen(themePanel, 'toggle', () => { if (themePanel.open && !diagLoaded && online()) void loadDiagnostics(); });
  listen(node('history'), 'toggle', () => { if (node('history').open && !historyLoaded) void listHistory(); });
  listen(node('history-search-form'), 'submit', event => { event.preventDefault(); void listHistory(); });
  listen(node('history-list'), 'change', () => { deleteTarget = ''; node('history-confirmation').hidden = true; void openHistory(); });
  listen(node('history-rename-form'), 'submit', renameHistory);
  listen(node('history-new'), 'click', newHistory);
  listen(node('history-delete'), 'click', () => {
    if (node('history-delete').disabled) return;
    deleteTarget = node('history-list').value; node('history-confirmation').hidden = false; node('history-keep').focus();
  });
  listen(node('history-confirm'), 'click', deleteHistory);
  listen(node('history-keep'), 'click', () => { deleteTarget = ''; node('history-confirmation').hidden = true; node('history-delete').focus(); });
  listen(node('history-first'), 'click', () => history.jump(false));
  listen(node('history-last'), 'click', () => history.jump(true));
  node('error').tabIndex = -1; node('status').tabIndex = -1; clearProbeConsent();
  node('kind').replaceChildren(...PROVIDER_KINDS.map(kind => option(kind, kind))); node('kind').value = 'OpenAI'; changeKind();
  listen(panel, 'toggle', () => {
    if (!panel.open) { node('key').value = ''; clearProbeConsent(); updateControls(); }
    else if (online() && !loaded && !busy) void listProviders();
  });
  listen(node('kind'), 'change', changeKind);
  listen(node('configure'), 'submit', configure);
  const modeChanged = () => {
    setMode(node('mode').value);
    for (const name of selectedSkills) if (!skillItems.some(skill => skill.name === name && skill.enabled && skill.modes.includes(node('mode').value))) selectedSkills.delete(name);
    renderSkills(); renderAgents(); updateControls();
  };
  listen(node('mode'), 'change', modeChanged);
  listen(node('apply-plan'), 'click', () => {
    if (node('apply-plan').hidden || node('apply-plan').disabled) return;
    node('mode').value = 'Agent';
    modeChanged();
    const announcement = document.getElementById('announcement');
    if (announcement) announcement.textContent = text('byokApplyPlanDone');
    if (!node('message').disabled) node('message').focus({ preventScroll: true });
  });
  for (const id of ['azure-subscription', 'azure-resource-group', 'azure-account', 'azure-tenant', 'azure-client']) listen(node(id), 'input', updateControls);
  listen(node('list'), 'click', listProviders);
  async function changeProvider() {
    if (!online() || busy) return;
    configurationId = node('providers').value;
    resetSelection(); cancelRemoveProvider(false);
    if (!activeConversation) { liveTurn = null; node('live').replaceChildren(); syncEmpty(); }
    await discover();
  }
  listen(node('providers'), 'change', () => void changeProvider());
  listen(node('discover'), 'click', discover);
  listen(node('remove'), 'click', requestRemoveProvider);
  listen(node('remove-confirm'), 'click', deleteProvider);
  listen(node('remove-keep'), 'click', () => cancelRemoveProvider());
  listen(node('azure-inventory'), 'click', requestAzureInventory);
  listen(node('models'), 'change', chooseModel);
  listen(node('model-select'), 'change', () => { node('models').value = node('model-select').value; void chooseModel(); });
  listen(node('apply-override'), 'click', override);
  listen(node('probe-consent'), 'change', updateControls);
  listen(node('probe-capability'), 'change', updateControls);
  listen(node('verify'), 'click', probe);
  for (const key of Object.keys(capabilityNames)) listen(node(key), 'change', updateControls);
  listen(node('message'), 'input', () => {
    suggestionIndex = 0; renderSkillSuggestions(); updateControls();
    if (/^\/[a-z0-9-]*$/.test(node('message').value) && !skillsLoaded && online()) requestSkills();
  });
  listen(node('message'), 'keydown', event => {
    if (event.isComposing) return;
    if (!node('skill-suggestions').hidden) {
      const matches = renderSkillSuggestions();
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); suggestionIndex = (suggestionIndex + (event.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length; renderSkillSuggestions(); return;
      }
      if (event.key === 'Enter' && !event.ctrlKey) { event.preventDefault(); chooseSkillSuggestion(matches[suggestionIndex].name); return; }
      if (event.key === 'Escape') { event.preventDefault(); node('skill-suggestions').hidden = true; node('message').removeAttribute('aria-activedescendant'); return; }
    }
    if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.metaKey) { event.preventDefault(); if (!node('send').disabled) node('chat-form').requestSubmit(); }
  });
  function openSettingsForError() {
    panel.open = true;
    if (['invalid_key', 'permission_denied'].includes(errorCode)) {
      node('key').focus({ preventScroll: true });
    } else if (['invalid_url', 'unsafe_url'].includes(errorCode)) {
      node('url').focus({ preventScroll: true });
    } else {
      node('configure').focus({ preventScroll: true });
    }
  }
  if (node('error-action')) listen(node('error-action'), 'click', openSettingsForError);
  if (node('history-error-action')) listen(node('history-error-action'), 'click', openSettingsForError);
  listen(document, 'keydown', event => {
    if (event.key === 'Escape' && !node('history-confirmation').hidden) {
      event.preventDefault();
      deleteTarget = '';
      node('history-confirmation').hidden = true;
      node('history-delete').focus({ preventScroll: true });
    } else if (event.key === 'Escape' && !node('remove-confirmation').hidden) {
      event.preventDefault();
      cancelRemoveProvider();
    }
  });
  listen(node('context-reference'), 'click', attachReference);
  listen(node('context-file'), 'click', () => captureContext('file', node('context-path').value.trim().replace(/^@(arquivo|file)(?:\s+|$)/i, '')));
  for (const kind of ['solution', 'selection', 'output']) listen(node(`context-${kind}`), 'click', () => captureContext(kind));
  async function handlePaste(event) {
    const items = event.clipboardData?.items;
    if (!items || !items.length) return;
    for (const item of items) {
      if (item.kind === 'file' || item.type?.startsWith('image/')) {
        const file = item.getAsFile?.() || item;
        if (file) {
          event.preventDefault();
          await processImageFile(file);
          return;
        }
      }
    }
  }
  listen(node('message'), 'paste', handlePaste);
  listen(node('chat-form'), 'paste', handlePaste);
  const composer = document.querySelector?.('.composer-panel') || node('chat-form');
  function handleDragOver(event) {
    const types = event.dataTransfer?.types;
    if (types && (types.includes ? types.includes('Files') : Array.from(types).includes('Files'))) {
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
      composer.classList?.add('drag-over');
    }
  }
  function handleDragLeave(event) {
    if (!composer.contains?.(event.relatedTarget)) {
      composer.classList?.remove('drag-over');
    }
  }
  async function handleDrop(event) {
    composer.classList?.remove('drag-over');
    const files = event.dataTransfer?.files;
    if (!files || !files.length) return;
    event.preventDefault();
    await processImageFile(files[0]);
  }
  listen(composer, 'dragenter', handleDragOver);
  listen(composer, 'dragover', handleDragOver);
  listen(composer, 'dragleave', handleDragLeave);
  listen(composer, 'drop', handleDrop);
  listen(node('remove-image'), 'click', () => { removeAttachment(); updateControls(); });
  listen(node('chat-form'), 'submit', submitChat);
  node('read-name').replaceChildren(...READ_TOOL_NAMES.map(name => option(name, name)));
  node('read-name').value = 'read_file'; changeReadTool();
  listen(node('read-name'), 'change', changeReadTool);
  listen(node('read-form'), 'submit', readDisk);
  listen(node('read-cancel'), 'click', cancel);
  node('write-name').replaceChildren(...WRITE_TOOL_NAMES.map(name => option(name, text(name === 'create_file' ? 'writeToolsCreate' : 'writeToolsEdit'))));
  node('write-name').value = 'create_file';
  listen(node('write-tools'), 'toggle', () => {
    if (node('write-tools').open) requestWriteStatus();
    else writeRefreshRequested = false;
  });
  listen(node('write-form'), 'submit', applyWrite);
  listen(node('write-undo'), 'click', undoWrites);
  listen(node('write-refresh'), 'click', () => { if (writeAllowed()) requestWriteStatus(); });
  listen(node('write-cancel'), 'click', cancel);
  listen(node('cancel'), 'click', cancel);
  if (!openTabs.length) openTabs.push({ id: null, title: '', modelId: selectedModelId, turnCount: 0 });
  renderAgents(); renderDiagnostics(); renderTabs(); refreshLocale(); setConnected(connectedState);
  return {
    refreshLocale, setConnected, bridge,
    dispose() {
      if (disposed) return;
      disposed = true; contexts = []; renderContexts(); node('key').value = ''; clearProbeConsent(); removeAttachment(); bridge.dispose();
      history.dispose(); listeners.forEach(remove => remove()); updateControls();
    }
  };
}
