# PESQUISA — Fontes verificadas para implementação

> Documento de referência. O agente implementador coda contra estes nomes exatos
> de endpoint, header e campo JSON. **Não invente campos.**
>
> Cada seção marca o nível de confiança. Onde está `RE-VERIFICAR`, confira ao vivo
> antes de codar e atualize este arquivo com a URL da fonte.
>
> Levantado em 2026-11-06.

---

# Parte 1 — BYOK: descoberta de modelos e capacidades

## 1.1 Endpoints de listagem

| Provedor | Endpoint | Auth | Metadado de capacidade? |
| --- | --- | --- | --- |
| OpenAI | `GET https://api.openai.com/v1/models` | `Authorization: Bearer` | **Não.** Só `data[].{id, object:"model", created, owned_by}` |
| Anthropic | `GET https://api.anthropic.com/v1/models` | `x-api-key` + `anthropic-version` | **Sim, rico** (ver 1.2) |
| Gemini | `GET https://generativelanguage.googleapis.com/v1beta/models?pageSize=&pageToken=` | `x-goog-api-key: KEY` | Parcial: `thinking`, limites de token. **Sem flag de visão** |
| OpenRouter | `GET https://openrouter.ai/api/v1/models` | pública, sem chave | **O mais rico** (ver 1.3) |
| Ollama | `GET /api/tags` → `POST /api/show {"model":"…","verbose":false}` | nenhuma por padrão | **Sim**, array `capabilities` |
| Azure OpenAI | `GET {endpoint}/openai/deployments?api-version=…` e `GET {endpoint}/openai/models?api-version=2024-10-21` | header `api-key:` | Parcial, sem visão/raciocínio |
| LM Studio | `GET /api/v0/models` → fallback `/v1/models` | `Authorization: Bearer` | `type:"vlm"\|"llm"\|"embeddings"`, `max_context_length` |
| DeepSeek, Groq, Mistral, xAI, Together, Cerebras, vLLM, llama.cpp | `GET /v1/models` (OpenAI-compatível) | Bearer | Só id/owned_by/created. vLLM também dá `max_model_len` |

**Azure:** o que você usa no chat é o **deployment name**, não o model id.
`/openai/models` retorna `capabilities:{chat_completion, completion, embeddings,
fine_tune, inference}`, `lifecycle_status`, `deprecation:{inference,fine_tune}`.

**OpenAI:** existe pedido de feature aberto (set/2026) por um objeto
`capabilities` / `GET /v1/models/{id}/capabilities`, sem compromisso da OpenAI.
Um endpoint não documentado `https://api.openai.com/dashboard/models` é citado
como expondo arrays de features — **NÃO VERIFICADO, auth por cookie de sessão.
Não implementar contra ele.** `RE-VERIFICAR` se a OpenAI publicou metadado oficial.

## 1.2 Anthropic — `ModelInfo` (a melhor fonte nativa)

Topo: `data[]`, `first_id`, `last_id`, `has_more`.
Query: `after_id`, `before_id`, `limit` (1–1000, default 20),
`lifecycle` (`active|deprecated|retired`).

Por modelo: `type:"model"`, `id`, `display_name`, `created_at`,
`max_input_tokens`, `max_tokens`, `lifecycle`, `deprecated_at`, `retires_at`,
`line`, e **`capabilities`**:

```
capabilities:
  image_input.supported          -> VISÃO
  pdf_input.supported
  batch.supported
  citations.supported
  structured_outputs.supported
  code_execution.supported
  thinking: { supported, types: { adaptive, between_tools, disabled, enabled } }
  effort:   { supported, low, medium, high, max, xhigh }
  server_tools: { supported, code_execution, web_search }
  context_management: { supported, clear_thinking_20251015,
                        clear_tool_uses_20250919, compact_20260112 }
```

Uma única chamada responde visão + thinking + níveis de effort + contexto.
**Use diretamente, sem heurística.**

`RE-VERIFICAR`: o valor exato do header `anthropic-version` na data do build, e os
nomes atuais de `line`. Fonte: <https://platform.claude.com/docs/en/api/models-list>

## 1.3 OpenRouter — verificado ao vivo

Topo: `data`, `total_count`, `links.next`.

Campos por modelo:
`id`, `canonical_slug`, `hugging_face_id`, `name`, `created`, `description`,
`context_length`, `architecture`, `pricing`, `top_provider`, `per_request_limits`,
`supported_parameters`, `default_parameters`, `knowledge_cutoff`, `links`,
`endpoints`, **`reasoning`**.

```
architecture: { modality, input_modalities[], output_modalities[],
                tokenizer, instruct_type }
   input_modalities valores: text | image | file | audio | video
   -> "image" presente = VISÃO

supported_parameters[]: tools, tool_choice, reasoning, reasoning_effort,
   include_reasoning, structured_outputs, response_format, max_tokens,
   temperature, top_p, seed
   -> "tools" presente = TOOL CALLING

reasoning: { mandatory: bool,
             supported_efforts: ["high","low"],
             default_effort: "low" }
   -> melhor campo para a UI: diz exatamente quais níveis permitir
      e se o raciocínio NÃO pode ser desligado

top_provider: { context_length, max_completion_tokens, is_moderated }
pricing: { prompt, completion, image, internal_reasoning,
           input_cache_read, input_cache_write, web_search, ... }
   ATENÇÃO: todos os preços são STRINGS, em USD por token.
endpoints[]: ex. ["/api/v1/chat/completions","/api/v1/messages","/api/v1/responses"]
```

Filtros server-side (use para não baixar tudo):
`?supported_parameters=tools&input_modalities=text,image&limit=1000&offset=0`
(`limit` máx 1000; pagine por `links.next`).

Fontes:
<https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties>
· <https://openrouter.ai/docs/use-cases/reasoning-tokens>

## 1.4 Gemini

Campos: `name`, `baseModelId`, `version`, `displayName`, `description`,
`inputTokenLimit`, `outputTokenLimit`, `supportedGenerationMethods[]`
(`generateContent`, `countTokens`, `embedContent`), **`thinking` (boolean)**,
`temperature`, `maxTemperature`, `topP`, `topK`.

- `thinking == true` → suporta raciocínio.
- **Visão não tem flag.** Todos os Gemini nativos são multimodais; `gemma-*` e
  variantes `*-image` / `*-live` precisam de registry.
- Filtre por `supportedGenerationMethods` contendo `generateContent` para
  descartar modelos de embedding.

Fonte machine-readable e **sem chave**, ideal para validar no build:
<https://generativelanguage.googleapis.com/$discovery/rest?version=v1beta>

## 1.5 Ollama

`GET /api/tags` só dá name/size/details — **é obrigatório** chamar
`POST /api/show` por modelo.

```
capabilities: ["completion","vision","tools","thinking","embedding"]
thinking: { values: [false,true] | ["low","high"], default: ... }
          (omitido quando não suportado)
details: { family, families, parameter_size, quantization_level }
model_info."<arch>.context_length"
model_info."<arch>.vision.*"   (presença => torre de visão)
```

Fonte: <https://docs.ollama.com/api-reference/show-model-details>

## 1.6 Estratégia de detecção em camadas (obrigatória)

Nesta ordem. **Nunca comece probing.**

1. **Verdade nativa do provedor (custo zero).** Anthropic `capabilities`,
   OpenRouter `architecture`+`supported_parameters`+`reasoning`, Gemini
   `thinking`, Ollama `/api/show`, LM Studio `/api/v0/models`.
2. **Registry curado** em JSON, chave `(providerId, regex do modelId)`, para
   OpenAI/Azure/Groq/Mistral/xAI/DeepSeek/Together/Cerebras. Campos:
   `{vision, tools, reasoning:{kind, efforts[], mandatory}, contextWindow,
   maxOutput, streaming}` mais `source`, `confidence`, `verifiedAt`.
   Deve ser atualizável sem release da extensão.
3. **Heurística de nome** — só fallback, marcada como "não verificado" na UI:
   `-vision`/`-vl`/`gpt-4o`/`claude-*` ⇒ visão;
   `o\d`/`-thinking`/`-reasoner`/`r1`/`qwq` ⇒ raciocínio.
   **Nunca desabilite uma feature silenciosamente por heurística falhar** —
   ofereça toggle manual de override.
4. **Probing ativo preguiçoso — só no primeiro uso real, nunca ao colar a chave.**

### Regras do probing (proteção do crédito do usuário)

- Probe **apenas o modelo selecionado**, e **apenas a capacidade que será usada**.
  Provar N modelos × 3 capacidades são O(3N) chamadas pagas — OpenRouter sozinho
  lista 264+ modelos com tools.
- Probe mais barata: `max_tokens:1` (ou `max_completion_tokens:16`),
  `stream:false`; PNG 1×1 em data URI (~70 bytes) para visão; `tools:[{…}]`
  trivial + `tool_choice:"none"` para tools. Custo ≈ dezenas de tokens.
- Distinga **rejeição de capacidade** (400/422, `invalid_request_error`, mensagem
  citando o parâmetro) de erro de auth/quota.
- Cache resultado **negativo permanentemente** por model id; positivo com TTL.
- **Não probe raciocínio enviando um effort:** muitos modelos ignoram parâmetro
  desconhecido silenciosamente, então HTTP 200 não prova nada — e uma chamada de
  raciocínio bem-sucedida **cobra tokens de raciocínio**. Prefira registry /
  metadado nativo. Último recurso: `effort:"minimal"` ou `"none"`.
- Orçamento rígido: máx 1 probe por capacidade por modelo, cap global (ex. 5)
  por sessão, setting opt-in "Verificar capacidades (consome alguns tokens)".

## 1.7 Mapeamento de raciocínio entre provedores

| Provedor | Campo no wire | Valores |
| --- | --- | --- |
| OpenAI | Chat: `reasoning_effort` · Responses: `reasoning:{effort, summary}` | `none, minimal, low, medium, high` — **varia por modelo**; alguns rejeitam o parâmetro inteiro |
| Anthropic | `thinking:{type, budget_tokens}` e/ou effort | `type`: `adaptive`, `enabled`+`budget_tokens`, `disabled`, `between_tools`. Modelos novos **rejeitam** `budget_tokens` (400). `max_tokens` deve exceder o budget |
| Gemini | `generationConfig.thinkingConfig.{thinkingLevel, thinkingBudget, includeThoughts}` | `thinkingLevel`: `MINIMAL, LOW, MEDIUM, HIGH` (3+) — **erro em modelos anteriores**. `thinkingBudget` int para 2.5 (`-1` dinâmico, `0` desliga em Flash; 2.5 Pro não desliga) |
| OpenRouter | `reasoning:{effort \| max_tokens, enabled, exclude}` | effort unificado; resposta traz `reasoning_details[]` com `type` e `format` |
| DeepSeek | escolha do modelo `deepseek-reasoner`; saída `reasoning_content` | sem knob de effort |
| Qwen / vLLM | `chat_template_kwargs:{enable_thinking:bool}`; saída `reasoning_content` | servidor precisa de `--reasoning-parser`; não é campo padrão OpenAI |

**Abstração unificada do Lumen:**
`ReasoningIntent = Off | Minimal | Low | Medium | High | Max`.

O adapter mapeia para o nível suportado mais próximo usando metadado nativo
(`effort.*` / `reasoning.supported_efforts` / enum `thinkingLevel`) e
**omite o campo inteiro** quando não suportado, em vez de mandar um default.
Em modelo desconhecido, arredonde **para baixo**.

`RE-VERIFICAR` no build: enums exatos de effort por model id; quais modelos
rejeitam `budget_tokens`; se `thinkingBudget` ainda é aceito.
Fontes: <https://platform.claude.com/docs/en/build-with-claude/adaptive-thinking>
· <https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/thinking>
· <https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning>

## 1.8 Formato de entrada de visão

- **OpenAI:** `content:[{type:"image_url", image_url:{url:"data:image/png;base64,…", detail:"low|high|auto"}}]`.
  Responses API usa `input_image`. PNG/JPEG/GIF/WebP.
- **Anthropic:** `{type:"image", source:{type:"base64", media_type:"image/png", data:"…"}}`;
  também `source:{type:"url", url}`. Limites: **10 MB** por imagem, máx **8000×8000**,
  100 imagens (modelos 200k) / 600 nos demais, **128 MB** por request.
  Fonte: <https://platform.claude.com/docs/en/build-with-claude/vision>
- **Gemini:** `parts:[{inline_data:{mime_type:"image/png", data:"<base64>"}}]`
  (camelCase `inlineData`/`mimeType` também aceito); `file_data:{file_uri, mime_type}`
  para Files API. `RE-VERIFICAR` o cap atual de bytes inline (~20 MB total).

## 1.9 Normalização de streaming

**Wire de cada provedor:**

- **OpenAI Chat:** `data: {…choices[].delta…}` … `data: [DONE]`.
  Tool calls: `delta.tool_calls[].{index, id, function:{name, arguments}}` —
  concatene `arguments` por `index`.
  **Responses API:** eventos tipados `response.output_text.delta`,
  `response.function_call_arguments.delta`, `response.completed`.
- **Anthropic:** eventos nomeados `message_start`, `content_block_start`,
  `content_block_delta`, `content_block_stop`, `message_delta`, `message_stop`,
  `ping`, `error`. Deltas: `text_delta.text`, `thinking_delta.thinking`,
  `signature_delta.signature`, `input_json_delta.partial_json`.
  Tool use: o block start traz `{id,name,input:{}}`; concatene `partial_json` por
  `index` e parseie no `content_block_stop`. `usage` em `message_delta` é cumulativo.
- **Gemini:** `POST /v1beta/models/{model}:streamGenerateContent?alt=sse` →
  `data: {candidates:[{content:{parts:[…]}}]}`. Parts com `thought:true` são
  resumo; `thoughtSignature` **precisa ser replayado**. `functionCall` chega
  inteiro, sem delta de argumentos.

**Eventos internos do Lumen (normalize para isto):**

```
TextDelta | ReasoningDelta
ToolCallStart{id,name} | ToolCallArgsDelta{id,chunk} | ToolCallEnd{id,parsedArgs}
Usage{in,out,reasoning} | Done{finishReason} | Error
```

Mantenha um mapa de blocos indexado pelo índice do provedor. Guarde **verbatim**
o estado opaco do provedor (Anthropic `signature`, Gemini `thoughtSignature`,
raciocínio encriptado da OpenAI) para replay no turno seguinte.
**Ignore tipos de evento desconhecidos** — não quebre o stream por isso.

Fonte: <https://platform.claude.com/docs/en/build-with-claude/streaming>

## 1.10 Validação de chave e storage

**Valide com a própria chamada de listagem** — é grátis, não consome tokens:
OpenAI/OpenRouter/Groq/Mistral/xAI/Together/Cerebras `GET /v1/models`;
Anthropic `GET /v1/models?limit=1`; Gemini `GET /v1beta/models?pageSize=1`;
Azure `GET /openai/deployments?api-version=…`;
Ollama/LM Studio/vLLM `GET /api/tags` / `/api/v0/models`.

**Interpretação dos erros:**

| Código | Significado | Ação na UI |
| --- | --- | --- |
| 401 | chave inválida/revogada | rejeitar, pedir chave |
| 403 | região/permissão/modelo não liberado | rejeitar com mensagem específica |
| 404 | **base URL errada** (muito comum em BYOK) | dizer "URL incorreta", não "chave inválida" |
| 429 | quota/rate limit | **chave é válida** — não rejeitar; backoff |
| 402 | OpenRouter sem crédito | avisar saldo |

Headers úteis: OpenAI `x-ratelimit-{limit,remaining,reset}-{requests,tokens}`,
`retry-after`, `openai-organization`. Anthropic
`anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset}`
(reset em RFC 3339), `retry-after`, `request-id`.
Honre `retry-after` com backoff + jitter.

**Storage no Windows — ordem de preferência:**

1. **Windows Credential Manager** via `CredWrite`/`CredRead`,
   `CRED_TYPE_GENERIC`, target `Lumen/<providerConfigurationId>`, segredo no
   `CredentialBlob`. Vantagens: roaming-aware, auditável pelo usuário, uma
   entrada por provedor.
2. **DPAPI** — `System.Security.Cryptography.ProtectedData.Protect(bytes,
   optionalEntropy, DataProtectionScope.CurrentUser)`
   (NuGet `System.Security.Cryptography.ProtectedData` em .NET moderno).

Settings JSON guarda **só metadado não-secreto**: base URL, modelo selecionado,
cache de capacidades.

**Por que nunca plaintext:** settings sincronizam para perfil roaming e source
control, entram em backup/crash dump, e são legíveis por qualquer processo do
usuário. DPAPI é **escopo de usuário, não de processo** — qualquer código dentro
do `devenv.exe` consegue decifrar. É defesa contra roubo de disco e outros
usuários, **não** contra extensão maliciosa.

Fontes: <https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection>
· <https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords>

## 1.11 Re-verificar obrigatoriamente no build

A linha de modelos muda rápido. Antes de codar, confirme:

1. Enums exatos de `reasoning_effort` / effort por model id.
2. Se a OpenAI finalmente publicou metadado de capacidade.
3. String atual de `anthropic-version`.
4. `api-version` do Azure e se deployments expõem modalidades.
5. Cap de bytes de imagem inline no Gemini.
6. Campo `capabilities` do LM Studio (citado por terceiros, **não verificado**
   na doc oficial).

Fontes machine-readable e sem chave, ideais para validação/codegen no build:
- Discovery do Gemini: `https://generativelanguage.googleapis.com/$discovery/rest?version=v1beta`
- OpenRouter: `https://openrouter.ai/api/v1/models`

---

# Parte 2 — Visual Studio 18: extensibilidade

> Fatos locais verificados estão em `ARQUITETURA.md` §1. Aqui ficam os fatos de
> documentação/pesquisa, com URL. O caminho escolhido é **VSSDK clássico net48 +
> WebView2** (`ARQUITETURA.md` §2).

## 2.1 Os dois modelos de extensão

| | VSSDK clássico (in-process) | VisualStudio.Extensibility (out-of-process) |
| --- | --- | --- |
| TFM no VS 2026 | `net472` / `net48` | **`net10.0-windows8.0`** (VS 2022: .NET 8) |
| Pacotes | `Microsoft.VisualStudio.SDK` **17.14.40265**, `Microsoft.VSSDK.BuildTools` **18.9.820** — **no nuget.org** | `Microsoft.VisualStudio.Extensibility.Sdk` **18.12.1488** + `.Build` **18.12.1487** — **NÃO no nuget.org** (nuget.org só tem 17.14.x); vêm com o workload |
| Project SDK | `Microsoft.NET.Sdk` clássico | `Microsoft.VisualStudio.Sdk.Build` com `<TargetFrameworks>vs18_11</TargetFrameworks>` |
| UI | WPF/WebView2 arbitrários, **code-behind completo** | **Remote UI**: MVVM-only |
| Suporte no 18.x | **total** | total, exige 18.12+ se buildado com pacote 18.12 |

**Fato crucial verificado:** o shell do VS 2026 **continua em .NET Framework**,
não .NET 10. Extensões in-process são limitadas a .NET Framework; o SDK moderno
mapeia `vs18_11` in-proc/híbrido para **.NET Framework 4.8**.
Fonte: <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/how-it-works/models/extensibility-models?view=visualstudio>

Extensões `net472` do VS 2022 "simplesmente funcionam" no 2026 — só o
**lower bound** do manifesto importa.
Fonte: <https://devblogs.microsoft.com/visualstudio/modernizing-visual-studio-extension-compatibility-effortless-migration-for-extension-developers-and-users/>

Exemplo real publicado buildando VSIX `net472` via `dotnet build` com
`Microsoft.VSSDK.BuildTools` **17.14.2120**:
<https://github.com/adospace/vs-agentic/blob/main/CONTRIBUTING.md>

Docs do SDK moderno:
<https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/get-started/install-tools?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/ship/create-vsix-with-project-sdk?view=visualstudio>
· amostra: <https://github.com/microsoft/VSExtensibility/blob/preview_18.12/New_Extensibility_Model/Samples/SettingsSample/SettingsSample.csproj>

## 2.2 Remote UI — as restrições que motivaram a escolha

Verificado na doc oficial:

- XAML `DataTemplate` é enviado ao VS; **MVVM-only**.
- DataContext precisa ser serializável com `[DataContract]`/`[DataMember]`;
  usar `INotifyPropertyChanged` / `NotifyPropertyChangedObject`, `ObservableList<T>`.
- Comandos são **`IAsyncCommand` / `AsyncCommand`**, não `ICommand`
  (são proxied para ICommand in-proc).
- **Sem code-behind. Sem controles custom ou de terceiros. Sem merged resource
  dictionaries** (usar `ResourceDictionaries.AddEmbeddedResource`).
- Event handlers não suportados, exceto
  `vs:ExtensibilityUICommands.EventHandlers`.
- O XAML instancia dentro do devenv, então pode referenciar assemblies do
  processo do VS (ex.: `Microsoft.VisualStudio.Shell.15.0` para
  `VsResourceKeys`/`VsBrushes`).
- **WebView2 É possível** — confirmado pela Microsoft: adicionar xmlns
  `Microsoft.Web.WebView2.Wpf` + o controle. Fix de render em branco =
  `CoreWebView2CreationProperties.UserDataFolder` explícito.
  **Ressalva:** obter referência do controle WebView2 para interop JS a partir
  de um `RemoteUserControl` ficou **sem solução** naquela thread.

Fontes:
<https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/how-it-works/remote-ui?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/how-it-works/advanced-remote-ui?view=visualstudio>
· <https://learn.microsoft.com/en-us/answers/questions/2149139/how-to-add-webview2-to-a-visualstudio-extensibilit>

## 2.3 APIs por necessidade

| Necessidade | VSSDK clássico (**caminho do Lumen**) | Out-of-process |
| --- | --- | --- |
| Tool window de chat | `ToolWindowPane` + WPF/WebView2 arbitrário | `ToolWindow` + `RemoteUserControl` |
| Editar doc aberto com undo | `ITextBuffer` / `ITextEdit`, undo transactions | `Extensibility.Editor().EditAsync(batch)` — atômico, undo implícito |
| Doc ativo / seleção | `IVsTextManager.GetActiveView`, MEF `IWpfTextView.Selection` | `context.GetActiveTextViewAsync`, `textView.Selections` |
| Arquivos/projetos da solução | `IVsSolution`, `IVsHierarchy`, `DTE.Solution` | `Workspaces().QueryProjectsAsync`, `UpdateProjectsAsync`, `BuildAsync` |
| **Rodar shell + output** | **`Process` + `IVsOutputWindowPane`** | **`Process` + `Extensibility.Views().Output.CreateOutputChannelAsync`** |
| Error List | `ErrorListProvider` / `IVsErrorList` | via editor parts |
| Settings | `DialogPage` | `SettingCategory`, `Setting.*`, `ReadEffectiveValueAsync`/`WriteAsync`, Unified Settings |
| **Store seguro de segredo** | **não existe API pública documentada** → Credential Manager / DPAPI | idem (`IVsCredentialStoreService` é interno do VS) |
| Theming | `VsColors` / `VsResourceKeys`, `VSColorTheme.ThemeChanged` | `VsResourceKeys`/`VsBrushes` DynamicResources; VS 2026 aceita `.vstheme` |
| Atalhos / menus | `.vsct` + `OleMenuCommandService` | `CommandConfiguration.Shortcuts`, `Placements`, `CommandPlacement.VsctParent` |

**Não existe API pública de terminal em NENHUM dos dois modelos.** O
`Microsoft.VisualStudio.Terminal.dll` local é um serviço pty interno do
ServiceHub — **não verificado** para uso por extensão. Usar `Process`.

Não disponível out-of-process (irrelevante para o Lumen): IntelliSense
providers, light bulbs, custom editors, project systems.

Fontes:
<https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/recipes/editor/editor?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/recipes/query-projects?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/recipes/settings?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/recipes/themes-colors-fonts?view=visualstudio>
· <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/recipes/commands/commands?view=visualstudio>
· <https://stackoverflow.com/questions/49214827/where-to-store-credentials-for-vsix-visual-studio-2017-extension>

## 2.4 IA nativa do VS 2026 — contexto competitivo

- **BYOK nativo desde 18.10** (preview, ligado por padrão, inclusive Community):
  Microsoft Foundry, OpenAI, Anthropic, Ollama + endpoints custom OpenAI/Ollama.
  O 18.11 moveu o chat inteiramente para o **Copilot SDK**.
  Fontes: <https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes>
  · <https://devblogs.microsoft.com/visualstudio/bring-your-own-model-visual-studio-chat/>
- **Não foi encontrada API pública para registrar um model provider** no chat
  nativo. `NÃO VERIFICADO` se existe.
- **API de extensão do Copilot emergente, preview e NÃO documentada**, verificada
  por inspeção de binário em
  `…\CommonExtensions\Microsoft\Microsoft.VisualStudio.Copilot.Contracts\Microsoft.VisualStudio.Extensibility.Copilot.dll` (v18.12):
  `CopilotExtensibility`, `ICopilotAgentSkillsProvider`,
  `ICopilotMarkdownAgentProvider`, `ICopilotSdkPluginProvider`,
  `ICopilotContextProvider`, `ICopilotResponder`,
  `CopilotAgentProviderConfiguration`.
  → **PROIBIDO usar** (`AGENTS.md` §7). Sem doc, sem contrato, só no Insiders.
- **MCP nativo** (17.14+/2026): servidores consumidos via `.mcp.json` de
  solução/usuário, com registries e OAuth. A doc contempla servidores
  "embutidos e distribuídos com a extensão", mas o mecanismo exato de registro
  por extensão é `NÃO VERIFICADO`.
  Fonte: <https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers?view=visualstudio>
- **Agent Skills nativo desde 18.5**: `SKILL.md` no padrão **agentskills.io**,
  lido de `.github/skills/`, `.claude/skills/`, `.agents/skills/`,
  `~/.copilot/skills/`.
  → **O Lumen adota o mesmo formato e diretórios** (`ARQUITETURA.md` §2.10).
  Fonte: <https://learn.microsoft.com/en-us/visualstudio/ide/copilot-agent-skills?view=visualstudio>

## 2.5 Build, debug, install

- Build: `dotnet build -c Release` (VSIX em `bin/<config>/`) ou build no VS.
  F5 deploya na Exp (`VsixDeployOnDebug=true`; `DeployExtension` deprecated).
- Debug: `devenv.exe /RootSuffix Exp`.
  Reset: `CreateExpInstance.exe /Reset /VSInstance=18.0 /RootSuffix=Exp`.
  Hive: `%LocalAppData%\Microsoft\VisualStudio\18.0_<instanceId>Exp`
  — nesta máquina o instanceId é **`25e762c6`** (verificado); nenhuma hive Exp
  existe ainda.
- Install: `Common7\IDE\VSIXInstaller.exe /q /a <file>.vsix` **daquela instância**;
  uninstall `/q /u:<IdentityId>`.
- **Manifesto:** VS 2026 ignora upper bounds → `Version="[17.0,)"` (2022+2026) ou
  `"[18.0,)"` (só 2026); `ProductArchitecture=amd64`.
- **Extensões pure out-of-process NÃO têm `extension.vsixmanifest`** — metadados
  vão em código e geram `extension.json`. O Lumen é clássico, então **tem**
  vsixmanifest normal.

### Gotchas de install (fonte: vs-agentic CONTRIBUTING)

- Mesma `Id`+`Version` → instalação **silenciosamente ignorada**. Desinstale antes.
- `obj\...\extension.vsixmanifest` **obsoleto** pode ser empacotado. Limpe `obj/`.
- Regressão de quiet mode no 17.14.27:
  <https://developercommunity.microsoft.com/t/11097999>

### Gotchas do VS Installer — **verificados na prática nesta máquina**

| Tentativa | Resultado | Causa |
| --- | --- | --- |
| `setup.exe modify … --passive --norestart --wait` | exit **87** | **`--wait` não existe** no installer 4.12.34 |
| idem sem `--wait`, shell não elevado | exit **5007** | `--passive`/`--quiet` exigem shell **já elevado** |
| `Start-Process -ArgumentList` com array | exit **1** | array perde aspas → `--installPath C:\Program` (truncado no espaço) |

Diagnóstico sempre pelo log `%TEMP%\dd_installer_*.log`, linha `Exit Code:`.
**O exit code do wrapper não é confiável** — reportou 0 quando o installer
retornou 87 e 5007.

Forma correta: `.ps1` com `-ArgumentList` recebendo **uma string única
pré-quotada** + `-Verb RunAs -Wait -PassThru`. Ver o apêndice de `PLANO.md`.

## 2.6 Re-verificar no build

1. Versões de `Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools` e
   `Microsoft.Web.WebView2` que restauram juntas e compilam em `net48` com o
   SDK .NET 11 RC. Fallback comprovado: BuildTools **17.14.2120**.
2. Se `Microsoft.NETFramework.ReferenceAssemblies` é suficiente para buildar
   `net48` sem o targeting pack instalado.
3. Flags exatas de `VSIXInstaller` para atingir a instância Insiders
   (`/instanceId`, `/sku`, `/version`).
4. Se a Microsoft publicou API de provider de modelo para o chat nativo.
5. Mecanismo de registro de servidor MCP por extensão.