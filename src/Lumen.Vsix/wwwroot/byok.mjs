import { ByokBridge, ByokError, PROVIDER_KINDS, PROBE_CAPABILITIES, PROBE_PROVIDER_KINDS, MAX_PROBE_BUDGET, IMAGE_TYPES, MAX_IMAGE_BYTES, normalizeBaseUrl, safeErrorCode } from './byok-bridge.mjs';

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
  invalid_image_type: 'byokErrorImageType', image_too_large: 'byokErrorImageSize', vision_not_supported: 'byokErrorVision', network_error: 'byokErrorNetwork'
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
  let statusKey = connectedState ? 'byokReady' : 'byokDisconnected', errorCode = null, attachment = null, attachmentGeneration = 0;
  let streaming = false, needsCancel = false, probing = false, remainingBudget = null;
  const listeners = [];
  const bridge = new ByokBridge(transport, { connected: () => connectedState && Boolean(connected()) && !disposed });
  const online = () => bridge.available && connectedState && Boolean(connected()) && !disposed;
  const selectedModel = () => models.find(model => model.id === selectedModelId);
  const listen = (target, event, handler) => { target.addEventListener(event, handler); listeners.push(() => target.removeEventListener(event, handler)); };
  const option = (value, label) => { const result = document.createElement('option'); result.value = value; result.textContent = label; return result; };
  const localized = (tag, key, className = '') => {
    const result = document.createElement(tag); result.dataset.byokText = key; result.textContent = text(key); result.className = className; return result;
  };

  function setStatus(key) { statusKey = key; node('status').textContent = text(key); }
  function clearError() { errorCode = null; node('error').textContent = ''; node('error').hidden = true; }
  function showError(code) {
    errorCode = safeErrorCode(code); node('error').textContent = text(errorLabel(errorCode)); node('error').hidden = false;
    if (errorCode === 'cancelled') { clearError(); setStatus('byokCancelled'); }
  }
  function removeAttachment() {
    attachment = null; attachmentGeneration++; node('image').value = ''; node('attachment').hidden = true;
    node('attachment').textContent = ''; node('remove-image').hidden = true;
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
  function syncEmpty() { node('empty').hidden = node('output').children.length > 0; }
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
    node('message').disabled = !enabled || !model || !configurationId || locked;
    node('image').disabled = !enabled || locked || !model?.vision;
    node('remove-image').disabled = locked;
    node('send').disabled = !enabled || locked || !model || !configurationId || !node('message').value.trim() || node('message').value.length > 4096;
    node('cancel').disabled = !enabled || cancelling || cancelRequested || (!busy && !needsCancel);
    node('probe-consent').disabled = !probeEligible();
    node('probe-capability').disabled = !probeEligible();
    node('verify').disabled = !canProbe();
    node('probe').setAttribute('aria-busy', String(probing && busy));
    node('configure').setAttribute('aria-busy', String(busy && !streaming));
    node('chat-form').setAttribute('aria-busy', String(streaming || imageLoading));
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
        updateControls();
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
    if (result.code) showError(result.code);
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
      node('output').replaceChildren(); syncEmpty();
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
  async function attachImage() {
    const file = node('image').files?.[0];
    removeAttachment();
    if (!file || !online() || busy || needsCancel || !selectedModel()?.vision) { updateControls(); return; }
    const generation = attachmentGeneration;
    imageLoading = true; clearError(); updateControls();
    try {
      const result = await readImageAttachment(file);
      if (disposed || generation !== attachmentGeneration || !online() || !selectedModel()?.vision) return;
      attachment = result; node('attachment').textContent = text('byokImageAttached'); node('attachment').hidden = false; node('remove-image').hidden = false;
    } catch (error) { if (!disposed && generation === attachmentGeneration) showError(error.code || 'invalid_image_type'); }
    finally { imageLoading = false; clearProbeConsent(); if (!disposed) updateControls(); }
  }

  function createTurn(value) {
    const pinned = scrollPinned();
    const article = document.createElement('article'); article.className = 'byok-turn';
    const user = localized('h3', 'byokUser'); const userText = document.createElement('p'); userText.className = 'byok-plain'; userText.textContent = value;
    const assistant = localized('h3', 'byokAssistant');
    const response = document.createElement('div'); response.className = 'byok-plain';
    const reasoning = document.createElement('details'); reasoning.hidden = true; reasoning.append(localized('summary', 'byokReasoning'));
    const thoughts = document.createElement('p'); thoughts.className = 'byok-plain'; reasoning.append(thoughts);
    const tools = document.createElement('div');
    const usage = document.createElement('p'); usage.className = 'secondary';
    article.append(user, userText, assistant, response, reasoning, tools, usage);
    node('output').append(article);
    while (node('output').children.length > 20) node('output').firstElementChild.remove();
    syncEmpty();
    if (pinned) node('output').scrollTop = node('output').scrollHeight;
    return { article, response, text: '', reasoning, thoughts, tools, calls: new Map(), usage };
  }
  function renderEvent(turn, event) {
    if (disposed) return;
    const pinned = scrollPinned();
    if (event.kind === 'TextDelta') {
      turn.text += event.text; turn.response.append(document.createTextNode(event.text));
    } else if (event.kind === 'ReasoningDelta') {
      turn.reasoning.hidden = false; turn.thoughts.append(document.createTextNode(event.text));
    } else if (event.kind === 'ToolCallStart') {
      const card = document.createElement('details'); card.className = 'byok-tool'; card.open = true;
      const title = localized('summary', 'byokToolNotice'); const name = document.createElement('p'); name.textContent = event.name;
      const state = localized('p', 'byokToolPending', 'secondary'); const args = document.createElement('pre'); args.tabIndex = 0;
      card.append(title, name, state, args); turn.tools.append(card); turn.calls.set(event.id, { args, state });
    } else if (event.kind === 'ToolCallArgsDelta') turn.calls.get(event.id).args.append(document.createTextNode(event.chunk));
    else if (event.kind === 'ToolCallEnd') {
      const call = turn.calls.get(event.id); call.args.textContent = JSON.stringify(event.arguments, null, 2);
      call.state.dataset.byokText = 'byokToolEnded'; call.state.textContent = text('byokToolEnded');
    } else if (event.kind === 'Usage') {
      turn.usage.replaceChildren(localized('span', 'byokUsage'), document.createTextNode(`: ${event.input} / ${event.output}`));
    } else if (event.kind === 'Error') showError(event.code);
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
  async function submitChat(event) {
    event.preventDefault();
    if (node('send').disabled || !online()) return;
    busy = true; streaming = true; clearError(); setStatus('byokStreaming'); updateControls();
    const value = node('message').value;
    const turn = createTurn(value);
    try {
      const request = bridge.request('byok.chat', { configurationId, text: value,
        effort: selectedModel()?.reasoning && selectedModel().efforts.length ? node('effort').value : 'Off',
        ...(attachment ? { image: attachment } : {})
      }, { onEvent: event => renderEvent(turn, event) });
      removeAttachment();
      const result = await request;
      if (disposed) return;
      if (result.completed) { node('message').value = ''; setStatus('byokCompleted'); }
      else if (!errorCode) showError('provider_error');
    } catch (error) {
      if (!disposed) {
        showError(error.code);
        if (error.code === 'timeout' || error.code === 'invalid_response' || error.code === 'stream_too_large') needsCancel = true;
      }
    } finally {
      busy = false; streaming = false; if (!cancelling) cancelRequested = false; clearProbeConsent();
      if (!disposed) { finishMarkdown(turn); updateControls(); if (!node('message').disabled) node('message').focus({ preventScroll: true }); }
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
    for (const root of [panel, node('chat-form'), node('output')]) {
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

  node('error').tabIndex = -1; node('status').tabIndex = -1; clearProbeConsent();
  node('kind').replaceChildren(...PROVIDER_KINDS.map(kind => option(kind, kind))); node('kind').value = 'OpenAI'; changeKind();
  listen(panel, 'toggle', () => {
    if (!panel.open) { node('key').value = ''; clearProbeConsent(); updateControls(); }
    else if (online() && !loaded && !busy) void listProviders();
  });
  listen(node('kind'), 'change', changeKind);
  listen(node('configure'), 'submit', configure);
  listen(node('list'), 'click', listProviders);
  listen(node('providers'), 'change', () => { configurationId = node('providers').value; resetSelection(); node('output').replaceChildren(); syncEmpty(); void discover(); });
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
  listen(node('image'), 'change', attachImage);
  listen(node('remove-image'), 'click', () => { removeAttachment(); updateControls(); });
  listen(node('chat-form'), 'submit', submitChat);
  listen(node('cancel'), 'click', cancel);
  refreshLocale(); setConnected(connectedState);
  return {
    refreshLocale, setConnected,
    dispose() {
      if (disposed) return;
      disposed = true; node('key').value = ''; clearProbeConsent(); removeAttachment(); bridge.dispose();
      listeners.forEach(remove => remove()); updateControls();
    }
  };
}
