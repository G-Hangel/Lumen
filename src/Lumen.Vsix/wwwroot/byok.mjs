import { VirtualHistory } from './virtual-history.mjs';
import { ByokBridge, ByokError, PROVIDER_KINDS, PROBE_CAPABILITIES, PROBE_PROVIDER_KINDS, MAX_PROBE_BUDGET, IMAGE_TYPES, MAX_IMAGE_BYTES, MAX_CONTEXTS, MAX_CONTEXT_TEXT, MAX_CONTEXT_TOTAL, normalizeBaseUrl, safeErrorCode } from './byok-bridge.mjs';

export const DEFAULT_BASE_URLS = Object.freeze({
  OpenAI: 'https://api.openai.com/v1/', OpenRouter: 'https://openrouter.ai/api/v1/',
  Anthropic: 'https://api.anthropic.com/v1/', Gemini: 'https://generativelanguage.googleapis.com/',
  Ollama: 'http://127.0.0.1:11434/', Azure: '', LMStudio: 'http://127.0.0.1:1234/'
});
const capabilityNames = { vision: 'Vision', tools: 'Tools', reasoning: 'Reasoning' };
const capabilityLabels = { vision: 'byokVision', tools: 'byokTools', reasoning: 'byokReasoning' };
const errorLabels = {
  invalid_key: 'byokErrorInvalidKey', permission_denied: 'byokErrorPermission', invalid_url: 'byokErrorUrl', unsafe_url: 'byokErrorUrl',
  rate_limited: 'byokErrorRate', insufficient_credit: 'byokErrorCredit', timeout: 'byokErrorTimeout',
  bridge_unavailable: 'byokErrorBridge', bridge_busy: 'byokErrorBusy', invalid_message: 'byokErrorFormat', invalid_response: 'byokErrorFormat',
  invalid_image_type: 'byokErrorImageType', image_too_large: 'byokErrorImageSize', vision_not_supported: 'byokErrorVision', network_error: 'byokErrorNetwork',
  history_corrupt: 'historyErrorCorrupt', history_too_large: 'historyErrorSize', history_write_failed: 'historyErrorWrite', conversation_not_found: 'historyErrorMissing',
  context_unavailable: 'contextErrorUnavailable', context_outside_workspace: 'contextErrorOutside',
  context_too_large: 'contextErrorSize', context_stale: 'contextErrorStale'
};
export const errorLabel = code => errorLabels[safeErrorCode(code)] || 'byokErrorUnknown';

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
  let connectedState = Boolean(connected()), disposed = false, busy = false, imageLoading = false, cancelling = false;
  let cancelRequested = false, cancelRequest = null;
  let configurations = [], models = [], configurationId = '', selectedModelId = '', loaded = false;
  let statusKey = connectedState ? 'byokReady' : 'byokDisconnected', errorCode = null, retryAfterSeconds = null, attachment = null, attachmentGeneration = 0;
  let streaming = false, needsCancel = false, probing = false, remainingBudget = null;
  const listeners = [];
  let contexts = [], contextBusy = false;
  const contextSize = () => contexts.reduce((sum, item) => sum + item.text.length, 0);
  const contextWithinBounds = () => contexts.length <= MAX_CONTEXTS && contextSize() <= MAX_CONTEXT_TOTAL
    && contexts.every(item => item.text.length <= MAX_CONTEXT_TEXT);
  const canCapture = () => online() && !busy && !imageLoading && !needsCancel && !cancelling
    && contexts.length < MAX_CONTEXTS && contextSize() < MAX_CONTEXT_TOTAL;
  let activeConversation = null, savedConversations = [], records = [], liveTurn = null, historyLoaded = false;
  let visible = { start: 0, end: 0 }, deleteTarget = '', historyGeneration = 0;
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
    attachment = null; attachmentGeneration++; node('image').value = ''; node('attachment').hidden = true;
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
    renderSaved(); node('history-list').value = summary.id; node('history-name').value = summary.title;
    renderHistoryStatus();
  }
  function resetHistory() {
    historyGeneration++; activeConversation = null; records = []; liveTurn = null; cached.clear(); failedPages.clear();
    node('live').replaceChildren(); history.setItems(records); syncEmpty(); renderHistoryStatus();
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
  async function openHistory() {
    const id = node('history-list').value;
    if (!id) return;
    await operate(async () => {
      const result = await bridge.request('byok.history.open', { conversationId: id, offset: 0, limit: 10 });
      if (disposed) return;
      resetHistory(); rememberSummary(result.conversation); records = Array(result.total).fill(null);
      cacheTurns(result.turns); node('output').scrollTop = 0; history.setItems(records); syncEmpty(); setStatus('byokReady');
    }, node('output'));
  }
  async function newHistory() {
    await operate(async () => {
      await bridge.request('byok.history.new', {});
      if (disposed) return;
      resetHistory(); node('history-list').value = ''; node('history-name').value = ''; setStatus('byokReady');
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
      renderSaved(); renderHistoryStatus(); setStatus('byokReady');
    });
  }
  async function deleteHistory() {
    if (!deleteTarget || node('history-confirmation').hidden) return;
    const id = deleteTarget;
    await operate(async () => {
      await bridge.request('byok.history.delete', { conversationId: id, confirm: true });
      if (disposed) return;
      savedConversations = savedConversations.filter(item => item.id !== id);
      if (activeConversation?.id === id) resetHistory();
      deleteTarget = ''; node('history-confirmation').hidden = true; renderSaved(); setStatus('byokReady');
    });
  }
  function scrollPinned() {
    const log = node('output');
    return log.scrollHeight - log.scrollTop - log.clientHeight < 48;
  }
  function updateControls() {
    const enabled = online(), locked = busy || imageLoading || needsCancel || cancelling;
    node('config-fields').disabled = !enabled || locked;
    node('configure-button').disabled = !enabled || locked;
    node('list').disabled = !enabled || locked;
    node('providers').disabled = !enabled || locked || !configurations.length;
    node('discover').disabled = !enabled || locked || !configurationId;
    node('models').disabled = !enabled || locked || !models.length || !configurationId;
    const model = selectedModel();
    for (const capability of Object.keys(capabilityNames)) node(capability).disabled = !enabled || locked || !model;
    node('apply-override').disabled = !enabled || locked || !model || Object.keys(capabilityNames).some(key => node(key).value === '');
    node('effort').disabled = !enabled || locked || !model?.reasoning || !model.efforts.length;
    const matchesHistory = !activeConversation || (activeConversation.configurationId === configurationId && activeConversation.modelId === selectedModelId);
    node('message').disabled = !enabled || !model || !configurationId || locked || !matchesHistory;
    node('image').disabled = !enabled || locked || !model?.vision;
    node('remove-image').disabled = locked;
    node('context-path').disabled = !enabled || locked;
    for (const id of ['context-file', 'context-solution', 'context-selection', 'context-output', 'context-reference']) node(id).disabled = !canCapture();
    for (const remove of node('context-list').querySelectorAll('button')) remove.disabled = !enabled || locked;
    node('context').setAttribute('aria-busy', String(contextBusy && busy));
    node('send').disabled = !enabled || locked || !model || !configurationId || !matchesHistory || !contextWithinBounds() || !node('message').value.trim() || node('message').value.length > 4096;
    for (const id of ['history-query', 'history-search', 'history-new']) node(id).disabled = !enabled || locked;
    node('history-list').disabled = !enabled || locked || !savedConversations.length;
    for (const id of ['history-name', 'history-rename', 'history-delete']) node(id).disabled = !enabled || locked || !node('history-list').value;
    for (const id of ['history-confirm', 'history-keep']) node(id).disabled = !enabled || locked;
    for (const id of ['history-first', 'history-last']) node(id).disabled = locked || !records.length;
    if (node('error-action')) node('error-action').disabled = !enabled || locked;
    if (node('history-error-action')) node('history-error-action').disabled = !enabled || locked;
    node('cancel').disabled = !enabled || cancelling || cancelRequested || (!busy && !needsCancel);
    node('probe-consent').disabled = !probeEligible();
    node('probe-capability').disabled = !probeEligible();
    node('verify').disabled = !canProbe();
    node('probe').setAttribute('aria-busy', String(probing && busy));
    node('configure').setAttribute('aria-busy', String(busy && !streaming));
    node('chat-form').setAttribute('aria-busy', String(streaming || imageLoading || (contextBusy && busy)));
    node('count').textContent = text('byokCount', { count: node('message').value.length, max: 4096 });
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
    node('models').replaceChildren(option('', text(models.length ? 'byokChooseModel' : 'byokEmptyModels')),
      ...models.map(model => option(model.id, modelLabel(model))));
    node('models').value = selectedModelId;
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
      loaded = true; configurationId = ''; resetSelection(); renderProviders();
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
  function changeKind() {
    const kind = node('kind').value;
    node('url').value = DEFAULT_BASE_URLS[kind]; node('url').placeholder = kind === 'Azure' ? text('byokAzureUrlPlaceholder') : '';
    node('provider-id').value = kind.toLowerCase(); node('key').value = '';
    node('deployments-group').hidden = kind !== 'Azure';
    node('key-label').textContent = text(kind === 'Ollama' || kind === 'LMStudio' ? 'byokKeyLocal' : 'byokKey');
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
        const request = bridge.request('byok.configure', {
          id, providerId: node('provider-id').value.trim(), kind: node('kind').value, baseUrl, key,
          ...(names.length ? { deploymentNames: names } : {})
        });
        key = '';
        const result = await request;
        if (disposed) return;
        configurationId = ''; resetSelection(); applyCatalog(result);
      } finally { key = ''; }
    }, node('models'));
  }
  async function chooseModel() {
    const modelId = node('models').value;
    selectedModelId = ''; clearProbeConsent(); removeAttachment(); renderCapabilities();
    if (!modelId) return;
    await operate(async () => {
      const result = await bridge.request('byok.select', { configurationId, modelId });
      if (disposed) return;
      configurations = configurations.map(config => config.id === result.configuration.id ? result.configuration : config);
      selectedModelId = result.configuration.selectedModelId; renderModels(); renderCapabilities(); setStatus('byokSelected');
      if (activeConversation && (activeConversation.configurationId !== configurationId || activeConversation.modelId !== selectedModelId)) resetHistory();
      syncEmpty();
    }, node('message'));
  }
  async function override() {
    if (node('apply-override').disabled) return;
    const modelId = selectedModelId;
    await operate(async () => {
      const result = await bridge.request('byok.override', { configurationId, modelId,
        ...Object.fromEntries(Object.keys(capabilityNames).map(key => [key, node(key).value === 'true'])) });
      if (disposed) return;
      models = models.map(model => model.id === result.model.id ? result.model : model);
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
    const response = document.createElement('div'); response.className = 'byok-plain';
    const reasoning = document.createElement('details'); reasoning.hidden = true; reasoning.append(localized('summary', 'byokReasoning'));
    const thoughts = document.createElement('p'); thoughts.className = 'byok-plain'; reasoning.append(thoughts);
    const tools = document.createElement('div');
    const usage = document.createElement('p'); usage.className = 'secondary';
    article.append(user, userText);
    if (contextText) {
      const details = document.createElement('details'); details.className = 'context-history';
      const content = document.createElement('pre'); content.className = 'byok-plain'; content.tabIndex = 0; content.textContent = contextText;
      details.append(localized('summary', 'contextHistory'), content); article.append(details);
    }
    article.append(assistant, response, reasoning, tools, usage);
    return { article, response, text: '', reasoning, thoughts, tools, calls: new Map(), usage, record: { userText: value,
      ...(contextText ? { contextText } : {}), hasImage: false, text: '', reasoning: '', tools: [], status: 'failed' } };
  }
  function renderEvent(turn, event) {
    if (disposed) return;
    const pinned = !turn.archived && scrollPinned();
    if (event.kind === 'TextDelta') {
      turn.text += event.text; turn.response.append(document.createTextNode(event.text));
    } else if (event.kind === 'ReasoningDelta') {
      turn.record.reasoning += event.text;
      turn.reasoning.hidden = false; turn.thoughts.append(document.createTextNode(event.text));
    } else if (event.kind === 'ToolCallStart') {
      const card = document.createElement('details'); card.className = 'byok-tool'; card.open = true;
      const title = localized('summary', 'byokToolNotice'); const name = document.createElement('p'); name.textContent = event.name;
      const state = localized('p', 'byokToolPending', 'secondary'); const args = document.createElement('pre'); args.tabIndex = 0;
      card.append(title, name, state, args); turn.tools.append(card); turn.calls.set(event.id, { args, state, name: event.name });
    } else if (event.kind === 'ToolCallArgsDelta') turn.calls.get(event.id).args.append(document.createTextNode(event.chunk));
    else if (event.kind === 'ToolCallEnd') {
      const call = turn.calls.get(event.id); call.args.textContent = JSON.stringify(event.arguments, null, 2);
      call.state.dataset.byokText = 'byokToolEnded'; call.state.textContent = text('byokToolEnded');
      turn.record.tools.push({ id: event.id, name: call.name, arguments: event.arguments });
    } else if (event.kind === 'Usage') {
      turn.usage.replaceChildren(localized('span', 'byokUsage'), document.createTextNode(`: ${event.input} / ${event.output}`));
    } else if (event.kind === 'Error') showError(event.code, event.retryAfter);
    if (pinned) node('output').scrollTop = node('output').scrollHeight;
  }
  function finishMarkdown(turn) {
    if (typeof renderMarkdown !== 'function' || !turn.text) return;
    // Only the main's sanitized renderer may return HTML; streaming is always text nodes.
    try {
      const html = renderMarkdown(turn.text);
      if (typeof html !== 'string') return;
      turn.response.innerHTML = html; turn.response.className = 'markdown';
      for (const block of turn.response.querySelectorAll('pre')) {
        block.tabIndex = 0; block.setAttribute('aria-label', text('byokCodeBlock'));
        const wrapper = document.createElement('div'); wrapper.className = 'code-block';
        const copy = localized('button', 'byokCopy', 'code-copy'); copy.type = 'button';
        block.replaceWith(wrapper); wrapper.append(block, copy);
        copy.addEventListener('click', async () => {
          const value = (block.querySelector('code') || block).textContent;
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
    } catch { /* A renderer failure must leave already received plain text intact. */ }
  }
  function renderArchived(record) {
    if (!record) return localized('article', 'historyLoading', 'history-placeholder');
    const turn = createTurn(record.userText, record.contextText); turn.archived = true;
    if (record.hasImage) turn.article.append(localized('p', 'byokImageAttached', 'secondary'));
    renderEvent(turn, { kind: 'TextDelta', text: record.text });
    if (record.reasoning) renderEvent(turn, { kind: 'ReasoningDelta', text: record.reasoning });
    for (const call of record.tools) {
      renderEvent(turn, { kind: 'ToolCallStart', id: call.id, name: call.name });
      renderEvent(turn, { kind: 'ToolCallEnd', id: call.id, arguments: call.arguments });
    }
    if (record.status !== 'completed') turn.article.append(localized('p', record.status === 'cancelled' ? 'historyCancelledTurn' : 'historyFailedTurn', 'secondary'));
    finishMarkdown(turn); return turn.article;
  }
  async function submitChat(event) {
    event.preventDefault();
    if (node('send').disabled || !online() || busy || imageLoading || needsCancel || cancelling || !selectedModel() || !configurationId
      || !contextWithinBounds() || !node('message').value.trim() || node('message').value.length > 4096
      || (activeConversation && (activeConversation.configurationId !== configurationId || activeConversation.modelId !== selectedModelId))) return;
    busy = true; streaming = true; clearError(); setStatus('byokStreaming'); updateControls();
    const value = node('message').value;
    const contextText = contexts.map(item => `[Context: ${item.label}]\n${item.text}`).join('\n\n');
    const turn = createTurn(value, contextText); turn.record.hasImage = Boolean(attachment);
    liveTurn = turn; node('live').replaceChildren(turn.article); syncEmpty();
    node('output').scrollTop = node('output').scrollHeight;
    try {
      const request = bridge.request('byok.chat', { configurationId, text: value,
        effort: selectedModel()?.reasoning && selectedModel().efforts.length ? node('effort').value : 'Off',
        ...(attachment ? { image: attachment } : {}),
        ...(contexts.length ? { contextIds: contexts.map(item => item.id) } : {})
      }, { onEvent: event => renderEvent(turn, event) });
      removeAttachment();
      const result = await request;
      if (disposed) return;
      contexts = []; renderContexts();
      turn.record.text = turn.text; turn.record.status = result.completed ? 'completed' : 'failed';
      rememberSummary(result.conversation);
      records.length = result.conversation.turnCount;
      cacheTurns([{ ...turn.record, index: result.conversation.turnCount - 1 }]);
      liveTurn = null; node('live').replaceChildren(); history.items = records; history.invalidate(); history.jump(true);
      if (result.completed) { node('message').value = ''; setStatus('byokCompleted'); }
      else if (!errorCode) showError('provider_error');
    } catch (error) {
      if (!disposed) {
        showError(error.code); historyLoaded = false;
        turn.article.append(localized('p', error.code === 'cancelled' ? 'historyCancelledTurn' : 'historyInterruptedTurn', 'secondary'));
        if (error.code === 'timeout' || error.code === 'invalid_response' || error.code === 'stream_too_large') needsCancel = true;
      }
    } finally {
      busy = false; streaming = false; if (!cancelling) cancelRequested = false; clearProbeConsent();
      if (!disposed) { if (liveTurn === turn) finishMarkdown(turn); updateControls(); queueMicrotask(pumpHistory); if (!node('message').disabled) node('message').focus({ preventScroll: true }); }
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
        if (recovering) { clearError(); setStatus('byokCancelled'); }
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
    for (const root of [panel, node('history'), node('chat-form'), node('output')]) {
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
    renderSaved(); renderHistoryStatus(); renderContexts();
    if (errorCode) showError(errorCode); updateControls();
  }
  function setConnected(value = connected()) {
    if (disposed) return;
    connectedState = Boolean(value) && Boolean(connected()) && bridge.available;
    if (!connectedState) {
      bridge.disconnect(); node('key').value = ''; loaded = false; configurationId = ''; configurations = []; resetSelection(); renderProviders();
      needsCancel = false; remainingBudget = null; renderProbeBudget(); setStatus('byokDisconnected');
    } else {
      setStatus('byokReady'); if (panel.open && !loaded && !busy) void listProviders();
    }
    updateControls();
  }

  const history = new VirtualHistory(node('output'), node('history-spacer'), node('history-items'), renderArchived, range => {
    visible = range; queueMicrotask(pumpHistory);
  });
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
  listen(node('list'), 'click', listProviders);
  listen(node('providers'), 'change', () => { configurationId = node('providers').value; resetSelection(); if (!activeConversation) { liveTurn = null; node('live').replaceChildren(); syncEmpty(); } void discover(); });
  listen(node('discover'), 'click', discover);
  listen(node('models'), 'change', chooseModel);
  listen(node('apply-override'), 'click', override);
  listen(node('probe-consent'), 'change', updateControls);
  listen(node('probe-capability'), 'change', updateControls);
  listen(node('verify'), 'click', probe);
  for (const key of Object.keys(capabilityNames)) listen(node(key), 'change', updateControls);
  listen(node('message'), 'input', updateControls);
  listen(node('message'), 'keydown', event => {
    if (event.ctrlKey && event.key === 'Enter' && !event.isComposing) { event.preventDefault(); if (!node('send').disabled) node('chat-form').requestSubmit(); }
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
    }
  });
  listen(node('context-reference'), 'click', attachReference);
  listen(node('context-file'), 'click', () => captureContext('file', node('context-path').value.trim().replace(/^@(arquivo|file)(?:\s+|$)/i, '')));
  for (const kind of ['solution', 'selection', 'output']) listen(node(`context-${kind}`), 'click', () => captureContext(kind));
  async function attachImage() {
    const file = node('image').files?.[0];
    await processImageFile(file);
  }
  listen(node('image'), 'change', attachImage);
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
  listen(node('cancel'), 'click', cancel);
  refreshLocale(); setConnected(connectedState);
  return {
    refreshLocale, setConnected,
    dispose() {
      if (disposed) return;
      disposed = true; contexts = []; renderContexts(); node('key').value = ''; clearProbeConsent(); removeAttachment(); bridge.dispose();
      history.dispose(); listeners.forEach(remove => remove()); updateControls();
    }
  };
}
