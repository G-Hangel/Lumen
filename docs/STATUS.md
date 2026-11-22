# STATUS — Lumen

Diário do projeto: andamento cronológico, evidências de execução e hotfixes.
Para o plano executável e critérios de aceite, veja [PLANO.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/PLANO.md).
Para rastreamento de bloqueios e impedimentos técnicos, veja [BLOQUEIOS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/BLOQUEIOS.md).
Para o detalhamento de fatias de interface em andamento, veja [MELHORIAS-UI.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/MELHORIAS-UI.md).

Atualizado em **2026-11-22**.

## Assinatura do VSIX via SignPath e correções de concorrência (2026-11-22)

1. **Race condition em `SettingsStore`.** `WriteModelOverrideEntryAsync` e
   `RemoveModelOverridesAsync` faziam read-modify-write do `providers.models.json`
   sem lock — escritas simultâneas perdiam overrides. Ambos agora passam por um
   `SemaphoreSlim` estático (`ModelsLock`) com `try/finally` de liberação.
2. **Bloqueio síncrono na UI.** `ByokSession.ApplyModelOverridesToModels` usava
   `ReadModelOverridesAsync(...).GetAwaiter().GetResult()`. Virou
   `ApplyModelOverridesToModelsAsync` com `await` + `CancellationToken`; os três
   call sites (`byok.configure`, `byok.discover` e o write do override) agora
   aguardam.
3. **`Task.Run` aninhado.** Removido o `await Task.Run(async ...)` dentro de
   `taskFactory.RunAsync` em `LumenControl.OnWebMessageReceived`.
4. **B17 — VS 2026 ignora `pkgdef` de extensão não assinada.** Confirmado com
   evidência nova: `ActivityLog.xml` registra
   `PkgDef section fails the security check` / `E_ACCESSDENIED` ao ler
   `Lumen.Vsix.pkgdef`, mesmo com `AllowUnsignedExtensions=1` nas chaves
   `ExtensionManager` (HKCU user e Exp). Detalhes e tentativas em
   `docs/BLOQUEIOS.md` (B17).
5. **Windows SDK instalado** (`winget install Microsoft.WindowsSDK.10.0.18362`);
   `signtool.exe` localizado em
   `C:\Program Files (x86)\Windows Kits\10\bin\10.0.18362.0\x64\`. O `signtool`
   rejeita o container `.vsix` ("file format cannot be signed") e o
   `dotnet nuget sign` rejeita certificado auto-assinado (`NU3018
   UntrustedRoot/NotValidForUsage`) — assinatura precisa ser de CA pública.
6. **SignPath.io (open source, gratuito).** Projeto criado com organization id
   `dc8e7b01-49c0-4747-85d5-c3e8956150ac`, repositório
   `https://github.com/G-Hangel/Lumen`, licença MIT. Artefato configurado como
   `<opc-file>` + `<opc-sign>` (equivalente a `VSIXSignTool.exe`), raiz direta
   no `.vsix` — sem zip intermediário. Workflow criado em
   `.github/workflows/sign-vsix.yml`: build Release → testes .NET → testes UI →
   upload com `archive: false` → `signpath/github-action-submit-signing-request@v3`
   → upload do artefato assinado. Dispara em `release` publicada ou manual.

Evidência (2026-11-22): build Release **0 erros, 0 avisos**; suíte .NET
**1.668/1.669** (a única falha é a ambiental pré-existente
`DetectedShellsExecuteOnlyAfterApproval` — `pwsh.exe` da Store retorna
`Acesso negado` no spawn, reproduzida isoladamente); UI **239/239**; pacote
**3/3**. Commits `f108e27` e `ad02415`.

Pendente: `SIGNPATH_API_TOKEN` (secret), `SIGNPATH_PROJECT_SLUG` e
`SIGNPATH_SIGNING_POLICY_SLUG` (repository variables) + publicação do repo para
o SignPath validar a origem; depois instalar o `.vsix` assinado e confirmar
**Exibir → Lumen** (fecha o B17).

## Melhorias de providers e esforço de raciocínio (2026-11-20)

1. **Effort XHigh.** Adicionado `ReasoningIntent.XHigh` ao enum unificado (minimal, low,
   medium, high, xhigh, max). Bridge `EFFORTS` atualizado; Ollama parseia `xhigh`;
   Gemini mapeia XHigh para High no wire; locale adicionou `byokEffortXHigh`.
2. **Configuração por modelo.** `byok.override` aceita `displayName` e `efforts` (lista),
   persiste em `providers.models.json` via `SettingsStore` (WriteModelOverrideAsync /
   ReadModelOverridesAsync / RemoveModelOverridesAsync). Overrides são aplicados no
   discover (`ApplyModelOverrides`). Remover provedor limpa os overrides.
3. **UI de configuração.** Painel de override ganhou campo "Nome de exibição" e
   checkboxes de níveis de esforço. Botão de remover provedor virou ícone de lixeira
   com confirmação existente.
4. **Controles negativos.** Override rejeita displayName > 128 e efforts fora da lista
   global; bridge valida `displayName` e `efforts`; persistência rejeita JSON inválido.

Evidência: build Release 0 erros/0 avisos; UI 238/238; .NET 1669/1669; pacote
3/3. VSIX reconstruído e instalado no hive Exp (57/57); hive regular bloqueado por
VS em execução (git.exe órfão encerrado; reinstalar após fechar o VS).
## Correção de build/pacote (2026-11-19)

`Menus.cto` (saída binária do VSCT) não estava sendo empacotado, embora o pkgdef
referencie `Menus.ctmenu`. Sem ele o command set nunca era registrado e
`View.Lumen` era inválido (`O comando "View.Lumen" não é válido`). O csproj agora
inclui o `.cto` via `VSIXSourceItem` num target `IncludeVsctBinary`
(`src/Lumen.Vsix/Lumen.Vsix.csproj:41-47`). Evidência: VSIX passou de 57 para 58
entradas e `View.Lumen` passou a executar sem exceção; `ActivityLog.xml` mostra
`End package load [LumenPackage]`.

## Correção do erro de correlação (2026-11-19)

Resposta parcial seguida de "Mensagem ou correlação da ponte inválida" não era
falha de provedor nem de correlação: `byok.mjs` gravava
`textNode.dataset.timestamp` num **Text node**, que não tem `dataset` no
Chromium. O `TypeError` no primeiro chunk derrubava a promessa como
`invalid_response`, e todos os chunks seguintes eram descartados (o host já os
havia enviado). Os chunks agora vão em `<span>` inerte. O adapter de DOM dos
testes passou a criar Text nodes sem `dataset`/`attributes`, como o navegador —
controle negativo: revertendo a correção a suíte volta a falhar.
Evidência em execução real: 2 turnos na mesma conversa, 2 tool calls,
3 `ToolExecution`, 0 eventos de erro, ambos `completed:true`.

## Estado atual

**Fases 0–9 concluídas; Fase 8 concluída (8a–8d; imagem pendente por decisão).**
O modo automático com retomada entrou em produção (2026-11-18, commit `eec6336`): o
orçamento configurável saiu do compositor e o loop de ferramentas pausa em vez de
falhar quando atinge um teto de segurança, preservando o que já foi feito e
oferecendo um botão **Continuar** para retomar. Veja a seção "Modo automático com
retomada" abaixo. A aceitação consolidada de 2026-11-12 (abaixo) fechou os
critérios integrais das Fases 3 e 4 na instância experimental, com as
limitações registradas. O modelo pode ler, criar/editar arquivos e executar
comandos com opt-in por envio. Escrita interna aplica automaticamente com diff;
undo reverte somente o último lote. Escrita externa exige aprovação por
operação; terminal exige aprovação, com opção de consentimento por sessão
revogada ao fechar/reabrir a solução. `apply_diff` e snapshot para buffer/disco,
diagnósticos e descoberta de shells estão no commit `dd826ea`. A Fase 5 tem
seleção de modos, políticas de chat, botão aplicar plano e compactação de
histórico, retry transitório sem reexecutar tools e paralelismo gated por
`ParallelTools` verificados (fatia 5c, abaixo); ContextBuilder integral
(@arquivos + contexto unificado) e o AC de tarefa real multi-passos verificados
na fatia 5d (abaixo).
A Fase 6 (Skills) está concluída: sistema extensível pelo usuário, com
progressive disclosure, acionamento explícito, manual e automático, UI completa
e exemplos embarcados — ACs verificados na Exp em 2026-11-16 (abaixo).
A Fase 7 (Personalização visual) foi fechada com as fatias 7a–7c e os ACs
confirmados na Exp em 2026-11-16, inclusive a troca real de tema do VS sem
reload (abaixo).
B9 foi encerrado após nova regressão readonly completa; a causa do timeout CDP
original não foi identificada. A Fase 2 mantém os limites aceitos em 2026-11-07:
Ollama local dispensado (B7). O inventário Azure (B8) foi resolvido em 2026-11-17
com duas vias: manual seguro (campos ARM na configuração BYOK) e OAuth Device
Code opt-in (MSAL, fluxo silencioso + Device Code, token Bearer ARM, sem
persistir credencial OAuth). Nesta fatia não houve chamadas pagas.

| Fase | Estado |
| --- | --- |
| Planejamento | concluído |
| Fase 0 — Spike de toolchain e UI | **concluído** |
| Fase 1 — Núcleo de domínio | **concluído** |
| Fase 2 — BYOK | **concluído (B8 resolvido: manual + OAuth Device Code opt-in)** |
| Fase 3 — UI do chat | **concluído (aceitação consolidada 2026-11-12)** |
| Fase 4 — Ferramentas e segurança | **concluído (limitações registradas abaixo)** |
| Fase 5 — Loop e modos | **concluído (skills ativas → Fase 6; limitações registradas abaixo)** |
| Fase 6 — Skills | **concluído (fatias 6a–6d; ACs verificados 2026-11-16)** |
| Fase 7 — Personalização visual | **concluído (fatias 7a–7c; ACs verificados 2026-11-16)** |
| Fase 8 — Hardening, empacotamento e entrega | **concluído (8a–8d; imagem pendente por decisão)** |
| Fase 9 — Subagentes e delegação | **concluído (9a–9d; ModelAgent com chamada de modelo via provedor)** |

## Modo automático com retomada (2026-11-18, commit `eec6336`)

O usuário considerou **não prático configurar os turnos e rodadas manualmente**. Em vez de
apenas esconder os campos, o comportamento mudou: o loop de ferramentas é automático e
quando atinge um teto de segurança **pausa**, preservando o que já foi feito.

### O que mudou
- `ToolBudget.Default` e os tetos do contrato subiram para **64 rodadas / 262144 tokens de
  saída / 256 invocações** (era 8/32768/32).
- O painel de orçamento foi removido do compositor (`index.html`); `readBudget()` saiu de
  `byok.mjs` e o payload de `byok.chat` não envia mais `budget`.
- `ToolChatLoop` agora sinaliza `Paused` e arquiva chamadas não executadas como
  `{"code":"deferred"}` em vez de lançar `tool_budget_exceeded`. O resultado acumulado por
  turno continua limitado a **131072 caracteres** (`MaxResultChars`), mas estourá-lo pausa
  em vez de falhar.
- `ByokSession`: deadline subiu de 2 para **10 minutos**; um deadline vira `paused`
  (o cancelamento do usuário continua `cancelled`); turnos pausados são salvos com status
  `paused` e `Resumable()` insere os `deferred` para que o histórico continue válido.
- Bridge: `byok.chat.result` aceita `paused` opcional; `historyTurn` aceita `paused`; Notice
  aceita `tool_budget_paused`.
- UI: notice localizada + botão **Continuar** no turno vivo e no histórico cacheado;
  `record.status = 'paused'`; helper `continuePaused`; status `byokPaused`.

### Controles negativos
- Testes .NET: detector de pausa marca corretamente um modelo **sem** visão; o parser de
  streaming sobrevive a evento desconhecido e JSON truncado; confinamento de path rejeita
  `../../`; budgets forjados (65 rodadas, 262145 tokens, 257 invocações) são rejeitados
  antes de capturar workspace/credenciais ou chamar o provedor.
- `ReadToolChatLoopTests` **104/104 verdes**; suíte completa **1663/1664**, com a única
  falha sendo a ambiental pré-existente de `pwsh` AppExecutionAlias.
- UI `npm run check:ui` **232/232**; `npm run check:package` **3/3**.

### Verificação na Exp (`25e762c6`)
VSIX reinstalado (SHA conferido) e estágio `plan-budget` reescrito: estado inicial sem campos
de orçamento, apply plan sem requests, budget explícito de 1 rodada **pausa** com notice
`tool_budget_paused` e resultado `deferred`, histórico retém o turno `paused`, retomada
completa na mesma conversa com o `deferred` reenviado ao provedor, budgets forjados rejeitados
pela ponte e layout 203 px sem overflow. **3 requests loopback, 0 pagos, 0 exceções JS**.
Fixture, solução e processos limpos ao final.

### Limites que permanecem
- O orçamento é automático; quem quiser um teto menor pode passar `budget` via RPC (a UI
  não expõe mais), mas o padrão é o máximo do contrato.
- Resultado acumulado por turno: 131072 caracteres; contexto máximo: 262144.
- Turno pausado é arquivado e reproduzido no próximo envio; turnos `failed`/`cancelled`
  ainda não são reaproveitados.

## Hotfix — turno encerrado cedo por orçamento padrão (2026-11-18)

Sintoma: modelos que já liam, editavam e executavam comandos paravam cedo com
`byokErrorToolBudget` (texto "Resposta interrompida...") sem concluir a tarefa.
Causa: o orçamento padrão do loop era **4 rodadas / 8192 tokens / 8 invocações**
(`ToolBudget.Default`). A conversa `25df7129-2940-46ae-9495-dbd2e740de26`
mostra dois turnos `failed`: 7 ferramentas executadas (~44.9 mil caracteres de
resultado) e um lote seguinte de 4 chamadas planejado; 7 + 4 = 11 invocações
ultrapassava o limite 8 e o loop lançava `tool_budget_exceeded` antes de
executar o lote.

Correção: `ToolBudget.Default` e os campos padrão da UI passam para o teto do
contrato, **64 rodadas / 262144 tokens / 256 invocações**. As mensagens
`byokErrorToolBudget` e `byokBudgetHelp` foram atualizadas para o modelo automático
(ver seção "Modo automático com retomada" abaixo).

Verificação: build Release **0 erros/0 avisos**; `npm run check:ui`
**232/232**; `npm run check:package` **3/3**; suíte .NET **1663/1664**, com a
única falha sendo a ambiental pré-existente de `pwsh` AppExecutionAlias em
`DetectedShellsExecuteOnlyAfterApproval` (executável `WindowsApps` retorna
`command_failed`). O outro teste de shell, `ShellExitAlsoKillsChildHoldingPipesOpen`,
falhou de forma intermitente em uma execução e passou no rerun, como já registrado
na Fase 9.

Verificação na Exp (`25e762c6`) após reinstalar o VSIX em `3f3qnkve.0hu` (SHA
conferido): o estágio `plan-budget` reescrito confirmou o compositor sem campos de
orçamento, apply plan sem requests, budget de 1 rodada **pausando** com notice
`tool_budget_paused` e resultado `deferred`, histórico retendo o turno `paused`,
retomada completando na mesma conversa e budgets forjados rejeitados pela ponte.
**3 requests loopback, 0 pagos, 0 exceções JS**. Fixture, solução e processos
limpos ao final.

Limites que continuam valendo: 64 rodadas é o máximo do contrato atual; o
resultado acumulado de ferramentas por turno continua limitado a 131072
caracteres; e `byok.chat` tem deadline global de 10 minutos.

## Hotfix BYOK — configuração de provedor rejeitada (2026-11-18)

Ao clicar em **Salvar / testar listagem**, a UI exibia `byokErrorFormat`
imediatamente, mesmo com a ponte conectada. Causa: `ByokSession.ConfigurationView`
serializa sempre o campo `arm` (`null` para provedores não-Azure), mas
`configuration()` em `byok-bridge.mjs` usava `fields()` com conjunto exato e não
aceitava `arm`. Consequentemente **toda** resposta com configuração
(`byok.configure.result`, `byok.list.result`, `byok.select.result`) falhava em
`isByokEnvelope` e chegava à UI como `invalid_response`.

Correção: `arm` passou a ser campo opcional e validado no contrato JS, reaproveitado
pela validação do request `byok.configure`. Novo teste cobre o formato real do host
com `arm: null` e com objeto ARM válido; controles negativos rejeitam campo extra,
GUID inválido, campo faltante e tipo inválido. `npm run check:ui`: **231/231**.

Verificação na Exp (`25e762c6`) via CDP, **0 chamadas pagas**: `byok.configure`
com Ollama local retornou `credentialValid: true` e listou `qwen3.5:2b-q4_K_M`;
`byok.select` e `byok.list` também passaram. As 29 duplicatas de Ollama criadas
durante o debug foram removidas, restando uma configuração com modelo selecionado.
Build Release **0 erros/0 avisos**, pacote **3/3**; suíte .NET **1.663/1.664**, com a
falha ambiental pré-existente de `pwsh` AppExecutionAlias reproduzida isoladamente.

## Fase 9 — fechamento (2026-11-17)

Verificação consolidada na Exp (`25e762c6`) com `scripts/verify-agents-webview.mjs`
e provedor loopback próprio, **0 chamadas pagas**: agente `review-helper`
descoberto em `.lumen/agents/` sem reiniciar, listado no painel, `mode_denied`
em Plan, e — no chat real — o modelo recebeu `delegate_agent` na lista de
ferramentas, chamou a ferramenta e o corpo do agente voltou como resultado
`tool` ao provedor; 0 exceções JS. Limpeza removeu provider, credencial,
conversas e fixture. O verificador tem estágios `verify`/`cleanup` e sempre
cria/remove seus próprios arquivos em `artifacts/agents-fixtures.json`.

Durante a verificação foi encontrado e corrigido um bug real: `ToolChatLoop`
rejeitava argumentos de qualquer ferramenta não conhecida, então o
`delegate_agent` do modelo chegava ao host como `invalid_arguments`. Agora
há validação explícita de `{name, task}` e um teste de sessão que prova o
fluxo ponta a ponta. Controle negativo adicional: uma chave sintética dentro do
resultado do subagente é barrada por `RejectSecret` antes do provider e gera
envelope `error`, sem vazamento no corpo enviado.

Build Release 0 erros/0 avisos; suíte .NET 1.651/1.651 (85 testes com filtro
`Agent`), UI 228/228, pacote 2/2. Uma execução da suíte falhou de forma
intermitente em `ShellExitAlsoKillsChildHoldingPipesOpen` (acesso concorrente a
`child.pid`); a repetição completa passou. `docs/PLANO.md`, que havia sido
acidentalmente truncado para uma seção de compressão de contexto, foi restaurado
do histórico e anotado com o escopo real da Fase 9.

**Limitações registradas (escopo original não coberto):** perfis de
pesquisa/planejamento/implementação, orçamento/custo por delegação, execução
paralela em lote com histórico e fontes próprias, cancelamento individual,
limites configuráveis ou delegação de escrita com diff/undo e resolução de
conflitos. Subagentes são instruções locais em `AGENT.md`, executados em
processo, sem ferramentas e sem aprovações próprias; a política de edição
continua exclusivamente no agente principal.

## Fase 9d — ModelAgent: chamada de modelo por subagente (2026-11-17)

**Objetivo:** resolver a limitação "não há chamadas de modelo por subagente"
permitindo que agentes definidos em `AGENT.md` especifiquem `provider`, `model`,
`max_tokens` e `max_turns` no frontmatter e executem chamadas reais de modelo
via sistema de provedores — **sem gastar créditos** (testes usam mocks/fakes; 0
chamadas pagas).

### Implementação

- **`AgentDefinition`** (`src/Lumen.Core/Agents/AgentDefinition.cs`): adicionados
  `ProviderKind`, `ModelId`, `MaxTokens`, `MaxTurns` e propriedade computada
  `HasModelConfig` (true quando `ProviderKind` e `ModelId` não nulos/vazios).
- **`AgentCatalog.Parse`** (`src/Lumen.Tools/AgentCatalog.cs`): lê os quatro
  campos do frontmatter YAML do `AGENT.md` e popula a definição. Validação:
  `ProviderKind` deve ser um `ProviderKind` válido, `ModelId` não vazio,
  `MaxTokens` 1–128k, `MaxTurns` 1–16. Diagnósticos acionáveis para campos
  inválidos/ausentes (`provider_invalid`, `model_missing`, `max_tokens_range`,
  `max_turns_range`).
- **`ModelAgent`** (`src/Lumen.Providers/ModelAgent.cs`): novo subagente que
  herda de `AgentBase` e implementa `ExecuteAsync` chamando o provedor
  correspondente via `IProviderFactory`. Mantém **histórico por agente** (lista
  de `ChatMessage`) isolado do chat principal, respeita `MaxTurns` (encerra
  com `error` se estourar) e `MaxTokens` (passa ao provedor como
  `max_output_tokens`). Retorna resultado padronizado `AgentResult` (sucesso/
  erro, texto, metadados de uso).
- **`ByokSession.RefreshAgentsAsync`** (`src/Lumen.Providers/ByokSession.cs`):
  instancia `ModelAgent` para agentes com `HasModelConfig == true` e registra no
  `AgentRegistry`. Agentes sem configuração de modelo continuam como
  `MarkdownAgent` (retorna só o corpo do AGENT.md).

### Validação na Exp (`25e762c6`, CDP porta `9223`)

Configuração testada:
- Ollama local em `http://127.0.0.1:11434` com modelo `qwen3.5:2b-q4_K_M`
  (capabilities: completion, vision, tools, thinking).
- Agente projeto em `artifacts/context-619aca88-cfc9-46d7-96bf-b0e458794b8b/.lumen/agents/ollama-coder/AGENT.md`:
  ```yaml
  ---
  name: ollama-coder
  description: A coding assistant that uses local Ollama model to help with code tasks
  provider: Ollama
  model: qwen3.5:2b-q4_K_M
  max_tokens: 2048
  max_turns: 3
  ---
  ```
- Fluxo CDP: `byok.discover` → `byok.select` (modelo) → `byok.chat` (modo Agent,
  mensagem pedindo delegação ao `ollama-coder`).
- Logs CDP confirmam: `ModelAgent` recebe a delegação, abre turno próprio,
  faz streaming `TextDelta` do Ollama, executa tool calls (ex.: `read_file`),
  completa turnos até `max_turns` ou finalização natural, devolve resultado ao
  chat principal como `tool` result. **0 chamadas a APIs pagas**.

### Controles negativos

- Agente com `provider: InvalidProvider` → diagnóstico `provider_invalid`,
  agente não registrado como `ModelAgent`.
- Agente com `model: ""` → diagnóstico `model_missing`, continua como
  `MarkdownAgent`.
- `max_tokens: 0` ou `>128000` → diagnóstico `max_tokens_range`.
- `max_turns: 0` ou `>16` → diagnóstico `max_turns_range`.
- `ModelAgent` atinge `MaxTurns` → encerra com `error: "max_turns_exceeded"`.
- Histórico do `ModelAgent` isolado: mensagens do subagente não vazam para o
  chat principal e vice-versa.
- `RejectSecret` no resultado do subagente: chave sintética injetada no
  corpo é barrada antes do provider, gera envelope `error` sem vazar.

### Build e testes

- Build Release: **0 erros / 0 avisos**.
- Suíte .NET: **1.662/1.664** aprovados (2 falhas pré-existentes, intermitentes,
  em `WorkspaceShellToolsTests` — `pwsh` AppExecutionAlias "Access denied",
  não relacionadas).
- Testes `Agent` filtrados: **85/85** verdes (inclui novo
  `ModelAgentTests.ModelAgentCallsProviderWithPerAgentHistoryAndTurnLimit` e
  `ModelAgentTests.ModelAgentStopsAtMaxTurns`).
- Testes UI: **228/228** verdes.
- Pacote VSIX: **2/2** testes de empacotamento verdes.

### Arquivos alterados/criados

- `src/Lumen.Core/Agents/AgentDefinition.cs` — campos de configuração de modelo
- `src/Lumen.Tools/AgentCatalog.cs` — parsing do frontmatter + diagnósticos
- `src/Lumen.Providers/ModelAgent.cs` — **novo** subagente com chamada de modelo
- `src/Lumen.Providers/ByokSession.cs` — instancia `ModelAgent` no refresh
- `src/Lumen.Vsix/wwwroot/byok.mjs` — correção field IDs (`byok-azure-*` →
  `azure-*`) que causava erro JS no painel BYOK
- `src/Lumen.Vsix/wwwroot/index.html` — campos ARM Azure + botão inventário

## Fase 9c — descoberta por pasta e painel de agentes (2026-11-17)

- `AgentCatalog` (`src/Lumen.Tools/AgentCatalog.cs`) descobre `AGENT.md` em
  `.lumen/agents/<nome>/` do projeto e `~/.lumen/agents/<nome>/` do usuário,
  com precedência projeto > usuário. Valida frontmatter (`name`, `description`),
  nome igual ao da pasta, corpo obrigatório, limites de tamanho e diagnosticos
  acionáveis (`name_mismatch`, `missing_frontmatter`, `duplicate_name`...).
  `WorkspaceGuard` rejeita junções para fora das pastas permitidas.
- `MarkdownAgent` devolve o próprio corpo como resultado da delegação —
  **texto não confiável**, sem ferramentas ou aprovações próprias.
  `AgentRegistry.Replace` troca o conjunto completo e descarta agentes antigos;
  o `ByokSession` atualiza a descoberta antes de cada chat no modo Agent e nos
  RPCs `byok.agents.list` / `byok.agents.delegate`.
- Painel localizado PT-BR/EN com lista, origem/escopo, diagnósticos, recarga e
  formulário de delegação. O botão só habilita no modo Agent; a ponte valida
  envelope, nomes, `task` e exige `mode === 'Agent'`.
- Build Release 0 erros/0 avisos; 83 testes com filtro `Agent`, suíte .NET
  1.649/1.649, UI 228/228, pacote 2/2. Exp `25e762c6` com fixture de projeto:
  agente `review-helper` descoberto sem reiniciar, lista no painel, delegação
  devolvendo as instruções e 0 exceções JS; `-Action Verify` passou.
- Escopo fechado para 9c; falta apenas a verificação consolidada dos ACs da
  Fase 9 (integração ponta a ponta com o modelo escolhendo `delegate_agent`).

## Fase 9b — delegação no loop de chat (2026-11-17)

- `AgentToolCatalog` expõe a ferramenta `delegate_agent` (apenas `name` e
  `task`, `additionalProperties:false`) e `AgentToolExecutor` a anexa ao
  executor de ferramentas **somente quando há agentes registrados**. A
  ferramenta é anunciada exclusivamente no modo Agent — `ToolChatLoop.SpecsFor`
  a remove em Ask/Plan, mantendo esses modos read-only.
- `ByokSession` aceita `AgentRegistry`/`DelegationCoordinator` opcionais,
  embrulha o executor com `AgentToolExecutor` no modo Agent e responde ao RPC
  `byok.agents.list` com `{name, scope}` por agente. A ponte JS valida o
  formato e rejeita nomes/path traversal ou scopes desconhecidos.
- Controles negativos: ferramenta ausente sem agentes e em Ask/Plan; argumentos
  ausentes/tipo errado/campo extra; agente desconhecido; tarefa vazia; lista
  forjada rejeitada pela ponte. Resultados de delegacao são dados não
  confiáveis (`isError` espelha `success`, sem conteúdo executável).
- Build Release 0 erros/0 avisos; 61 testes com filtro `Agent`, suíte .NET
  1.646/1.646, UI 227/227, pacote 2/2. Exp com o VSIX final: startup sem
  requisições externas e RPC `byok.agents.list` respondendo com lista vazia
  (a host ainda não registra agentes), 0 exceções; `-Action Verify` passou.
- **Limitação honesta:** nenhum agente é registrado na extensão nesta fatia,
  então `delegate_agent` não aparece no chat real. Isso é intencional: a
  descoberta de agentes por pasta e a UI de progresso/resultados ficam para 9c.

## Fase 9a — núcleo de subagentes e delegação (2026-11-16)

- `Lumen.Core.Agents`: `AgentBase` (execução, resultado padronizado, descarte),
  `AgentRegistry` (nomes `^[a-z0-9]+(-[a-z0-9]+)*$`, até 64 caracteres, sem
  duplicatas) e `DelegationCoordinator` (modo Agent obrigatório, profundidade
  máxima 2, tarefa até 4.096 caracteres, 4 delegações concorrentes, resultado
  até 65.536 caracteres).
- Controles negativos: nomes inválidos, duplicata, agente desconhecido,
  tarefa vazia/controlada/gigante, delegação em Plan/Ask, profundidade
  excedida, exceção do agente (sem vazar detalhe) e resultado nulo/trocar
  de nome/estourado — todos rejeitados com códigos estáveis
  (`invalid_agent_name`, `duplicate_agent`, `mode_denied`, `depth_exceeded`…).
- O registro descarta todos os agentes ao ser descartado; o coordenador
  independente libera seus semáforos sem destruir o registro.
- Build Release 0 erros/0 avisos; suíte .NET 1.639/1.639 (73 testes com filtro
  `Agent`), UI 226/226 e pacote 2/2. Exp com o VSIX final: startup com 13
  requisições em `lumen.local`, 0 externas/POST/exceções; `-Action Verify`
  confirmou o pacote. Nenhuma chamada paga.
- Escopo ainda aberto para 9b: integração da delegação ao loop de chat,
  descoberta de agentes por pasta e exposição de progresso/resultados na UI.

## Fase 8c — entrega e diagnóstico (2026-11-17)

- `LICENSE` MIT (copyright "Lumen contributors"), `CHANGELOG.md` com a versão
  0.1.0 e limitações, e `README.md` reescrito com instalação, primeiro uso,
  política de chave/telemetria e estado real do projeto. O guia de instalação
  foi incorporado ao README (sem documento extra).
- Manifesto do VSIX com display name **Lumen — Chat BYOK**, descrição PT-BR/EN
  e tags. Ícone e preview image ficaram pendentes por decisão do usuário (B12).
- Log estruturado em JSONL em `%LocalAppData%\Lumen\logs\lumen.log`
  (`LumenDiagnosticLog`): nível `Error|Warning|Info|Debug` persistido em
  `providers.log.json`, redação de `sk-`, `ghp_`, `AIza`, `Bearer` e
  `api_key`/`x-api-key`, e sem mensagem de exceção crua. RPCs
  `byok.log.get/set/open` com validação na ponte; os controles do painel
  Aparência só aparecem quando a host oferece o log. Nenhuma telemetria:
  `check:package` rejeita qualquer SDK/padrão de telemetria.
- **VSIX determinístico:** dois `clean`+`build` Release geraram o mesmo
  SHA-256 (`A4BFF307B83D11132EA39D4021549F4DCB0A2F52AD6BABDCFCD7C7C5A87E7AD1`).
  `scripts/normalize-vsix.ps1` congela timestamps dos entradas e o
  `extensionDir` aleatório do `manifest.json`/`catalog.json`; todas as
  assinaturas de arquivos já eram idênticas antes da normalização.
- Build limpo, suíte .NET 1.659/1.659, UI 230/230, pacote 3/3. Exp com o VSIX
  normalizado: painel Aparência leu `byok.log.get` (`Error`), botão “Abrir log”
  habilitado, 0 exceções JS; `-Action Verify` passou. Nenhum arquivo de nível
  de log foi criado na máquina (somente leitura).

## Fase 8d — carga e compatibilidade de ambiente (2026-11-17)

- Teste de carga offline `AgentToolSessionTests.LongConversationWithTwoHundredTwentyTurnsRemainsBounded`: 220 turnos, requests individuais limitados a 200 KB, compactação existente preservada por teste dedicado e execução em menos de dois minutos.
- Teste de muitos arquivos e limites de leitura já coberto por `WorkspaceReadToolsTests.ListingFindingAndLiteralSearchAreBounded`: listagem/busca bounded, 1.024 entradas visitadas, 4 MiB de scan e output bounded; arquivos binários e oversized são rejeitados.
- `WorkspaceShellToolsTests.VeryLargeCommandOutputIsTruncatedWithoutUnboundedMemory`: saída sintética de 200.000 caracteres truncada com `truncated=true` e payload abaixo de 65.536 caracteres.
- Verificação de UI/histórico real já cobre arquivo de histórico com 500 turnos, virtualização, páginas, alturas variáveis e layout negativo; não duplica dados sintéticos nem usa APIs pagas.
- `vswhere -prerelease -all` retornou somente `25e762c6` / Visual Studio Community 2026 Insiders. Não há VS 18 estável instalado; essa limitação foi registrada honestamente e não houve instalação alteradora.
- Build limpo: 0 erros/0 avisos; suíte .NET **1.661/1.661**, UI **230/230**, pacote **3/3**. `git status` limpo. O único item restante da Fase 8 é B12, ícone/preview image, adiado explicitamente pelo usuário.

## Fase 8b — revisão de segurança (2026-11-16)

- Lacuna encontrada e corrigida: `byok.configure` aceitava na host qualquer
  esquema de URL (a UI já rejeitava). `HttpTransport.ValidateBaseUrl` agora
  espelha `normalizeBaseUrl` do bridge: HTTPS em qualquer host, HTTP só em
  loopback, sem usuário/senha, query, fragmento, espaços ou barras invertidas.
  O erro é `invalid_url`, o mesmo contrato da UI, e nenhum pedido de rede é
  feito. `ProviderConfiguration` já validava o valor lido de settings, o que
  também foi confirmado por teste com arquivo adulterado.
- `Phase8SecurityTests` passou de 1 para 8 testes: casos negativos de
  `file:`, `ftp:`, credenciais na URL, query/fragmento e espaço, todos com
  **0 chamadas HTTP** e sem vazar a chave sintética na resposta.
- Demais vetores revisitados e ainda cobertos pelos controles existentes:
  `WorkspaceGuard` canonicaliza, rejeita `../`, dispositivos, aliases, junções
  e valida o handle aberto (`GetFinalPathNameByHandle`); shell usa lista de
  argumentos escapada, blocklist e aprovação; markdown usa `html:false`,
  allowlist de links, imagens desligadas e CSP sem `eval`/recursos externos;
  chaves ficam no Credential Manager/DPAPI e são redigidas em erros; a ponte
  valida envelope, origem, correlação e aprovações.
- Build Release 0 erros/0 avisos; suíte UI 226/226; `check:package` 2/2;
  suíte .NET 1.626/1.626 com `WindowsApps` removido do PATH do processo.
  Exp com o VSIX final: startup com 13 requisições apenas em `lumen.local`,
  0 externas, 0 POST, 0 exceções; `-Action Verify` confirmou o pacote.

## Fase 8a — pacote limpo e silêncio de rede (2026-11-16)

- `Phase8SecurityTests` cria uma sessão `ByokSession` com configurações vazias
  e um transporte HTTP que **lança se for chamado**. RPCs de onboarding
  (`byok.list`, `byok.theme.get`, `byok.skills.list`, `byok.history.list`,
  `byok.tool.write.status`) e pedidos que exigem provedor (`byok.chat`,
  `byok.discover`, `byok.probe`, `byok.configure` inválido) são respondidos
  localmente com **0 chamadas HTTP** e nenhum arquivo de configuração criado.
- `scripts/verify-package.mjs` (`npm run check:package`) extrai o VSIX e
  rejeita pastas `bin/obj/.git/.vs`, arquivos `.env`, `.pfx`, `.snk`, `.key`,
  `providers*.json` e padrões de credencial (`sk-`, `ghp_`, `AIza`, Bearer,
  atribuições de `api_key`/token). Controles negativos com canário, chave e
  `bin/` provam que o detector rejeita; o pacote atual passa sem achados.
- Verificação na Exp (`scripts/verify-offline-startup.mjs`): após recarregar o
  WebView com o domínio `Network` do CDP ativo, **14 requisições** ficaram
  apenas em `lumen.local`; **0 hosts externos, 0 POST e 0 exceções JS**. A
  configuração de provedor existente não foi alterada; por isso o cenário de
  máquina limpa sem chave é provado pelo teste C# isolado, não pela Exp.
- Build Release 0 erros/0 avisos; suíte UI 226/226; `check:package` 2/2;
  suíte .NET 1.619/1.619 com `WindowsApps` removido do PATH do processo (a
  falha ambiental conhecida do `pwsh` permanece com o PATH normal).
- VSIX final reinstalado e validado por `-Action Verify` (a verificação
  anterior acusou DLLs desatualizados após o rebuild; a reinstalação resolveu).
- Fora da 8a: paths, shell injection, markdown, broker e o restante do
  hardening/entrega seguem nas próximas fatias.

## Fase 7 — fechamento (2026-11-16)

Aceitação consolidada na instância experimental (`25e762c6`) com o VSIX de
HEAD (`f6dbd0b`, reinstalado e verificado ao final), sem chamadas pagas.
Mapeamento para os ACs do plano:

- **AC — trocar o tema do VS muda o chat na hora, sem reiniciar:** automação
  real do menu **Ferramentas → Tema → Claro/Escuro** no VS; fundo do WebView
  `#282828` → `#F9F9F9` → `#282828`, com o mesmo `performance.timeOrigin`
  (`1794859619699.4`), 6 eventos `theme.changed`, ponte sempre conectada e
  0 exceções JavaScript. Tema custom salvo era `null` antes e depois, então a
  troca exercitou o tema herdado do VS.
- **AC — tema custom aplicado e persistido entre sessões:** verificado na 7a
  (aplicação após recarregar o WebView sem ação do usuário, arquivo
  `%LocalAppData%\Lumen\providers.theme.json`).
- **AC — reset volta ao padrão:** verificado na 7a (RPC) e 7b (botão do painel).
- CNs: tema inválido rejeitado com `invalid_configuration` e preset anterior
  intacto; aviso de contraste calculado no host e exibido no painel;
  importação acima de 16 KB ou malformada rejeitada; fonte de 24 px com nome
  de 256 caracteres em 320×240 sem overflow; temas embarcados com AA aferido
  na 7c.
- Limpeza: o VS experimental foi encerrado (o fechamento gracioso expirou após
  a automação de tema, então o processo foi finalizado); o VSIX final foi
  reinstalado e `deploy-experimental.ps1 -Action Verify` confirmou todos os
  arquivos do pacote. Tema do VS restaurado para Escuro e nenhuma configuração
  de tema do usuário foi alterada.
- A única falha conhecida da suíte permanece a ambiental do `pwsh`
  AppExecutionAlias; com `WindowsApps` removido do PATH do processo de teste,
  a suíte completa passou (1.618/1.618).

## Fase 7c — importação, exportação e temas embarcados (2026-11-16)

- Importar JSON de até 16 KB carrega uma prévia sem persistir; arquivo inválido,
  campos desconhecidos e valores fora dos limites são rejeitados sem substituir
  a prévia. Exportar baixa a prévia válida em `lumen-theme.json`.
- Os presets `Lumen Dark` e `Lumen Light` podem ser escolhidos na UI. Testes
  aferem contraste WCAG AA para texto (4,5:1), foco e destaques (3:1),
  link/sintaxe e texto do botão (4,5:1), com controle negativo de contraste baixo.
  A validação JS aceita fontes Unicode e rejeita controles como o host C#.
- Build Release após clean: 0 erros e 0 avisos. Suíte de UI: 226 aprovados;
  suíte .NET: 1.618 aprovados com `WindowsApps` removido somente do PATH da
  execução para evitar o alias problemático de `pwsh`.
- Na Exp `25e762c6`, o painel selecionou tema embarcado, importou arquivo
  válido, rejeitou JSON inválido preservando a prévia e exportou um arquivo
  que foi lido e comparado ao original. A configuração salva foi comparada
  antes/depois e não mudou; sem exceções JS e sem APIs pagas.
- A verificação da troca real de tema do VS sem reload foi concluída no
  fechamento da Fase 7 (acima).

## Fase 7b — painel de aparência (2026-11-16)

- Painel localizado PT-BR/EN com prévia ao editar cores, fontes, tamanho,
  densidade, raio e destaques por modo; aplicar persiste, descartar ou fechar
  reverte a prévia e reset retorna ao tema do VS.
- Campos inválidos bloqueiam aplicação; contraste insuficiente exibe aviso.
  Cores parciais herdam individualmente do VS; a cor efetiva de texto é
  ajustada para contraste mínimo de 4,5:1.
- Build Release: 0 erros e 0 avisos. UI: 224 testes aprovados, incluindo
  controles negativos para tamanho inválido e contraste baixo. Suíte .NET:
  1.617 aprovados com `WindowsApps` removido apenas do `PATH` do processo de
  teste. Com o `PATH` normal, 1.616 aprovados e a falha ambiental já
  documentada em `DetectedShellsExecuteOnlyAfterApproval`. Após o clean, uma
  execução filtrada falhou por acesso concorrente a `child.pid` em
  `TimeoutAndCancellationKillLiveShellChildWithinDeadline`; a repetição
  completa passou (1.617/1.617).
- Exp `25e762c6`, sem APIs pagas: prévia, aviso, rejeição de tamanho 99,
  persistência de tamanho 24, descarte e reset exercitados no painel real,
  sem exceções JS. Em 320×240, fonte de 24 px e nome de fonte de 256
  caracteres não geraram overflow horizontal no formulário. O teste só
  gravou depois de confirmar que não havia tema salvo; reset restaurou o
  estado anterior. Verificação de empacotamento concluída.
- Na época faltavam importação/exportação e conferência dos temas embarcados,
  posteriormente cobertas pela 7c.

## Fase 7a — núcleo de tema custom (2026-11-16)

- `ThemePreset` (`src/Lumen.Core/Domain/ThemePreset.cs`): modelo do preset —
  cores, fonte do chat, fonte de código, tamanho, densidade, raio de borda e
  **cor de destaque por modo** (`modeAccent` ask/plan/agent). Todos os campos
  são opcionais; `null` herda o tema do VS.
- `ThemeCatalog` (`src/Lumen.Tools/ThemeCatalog.cs`): validação estrita
  (campo desconhecido, cor fora de `#rgb`/`#rrggbb`, tamanho 10–24, raio 0–24,
  densidade `compact|normal|spacious`, fonte restrita a caracteres de font-stack
  para não injetar CSS) e **contraste WCAG 2.1** com avisos
  (`low_contrast_text` < 4.5, `low_contrast_accent`/`low_contrast_mode_accent`
  < 3). As mesmas regras estão espelhadas em `byok-bridge.mjs`, então as duas
  pontas rejeitam o mesmo tema malformado.
- Persistência **por usuário**: `%LocalAppData%\Lumen\providers.theme.json`,
  gravado ao lado das configurações de provedor — nunca dentro da solução.
- RPCs `byok.theme.get` / `byok.theme.set` / `byok.theme.reset`, funcionando
  **sem provedor configurado**; `set` devolve o preset validado e a lista de
  avisos de contraste.
- Aplicação no WebView (`theme.mjs`): `applyPreset` materializa as CSS
  variables (`--vs-*`, `--chat-font`, `--mono`, `--font-size`, `--radius`,
  `--accent-ask|plan|agent`, `--mode-accent`) e o atributo `data-density`. Um
  preset **parcial** não troca as cores (herda do VS); a troca de tema do VS
  (`theme.changed`, sem reload) preserva o preset custom.
- Build Release **0 erros, 0 avisos**; suíte `.NET` **1.616 aprovados** (21
  de tema) e a única falha é a ambiental pré-existente do `pwsh`; `npm run
  check:ui` **222/222**.
- Verificação na Exp (`25e762c6`) via `scripts/verify-theme-webview.mjs`:
  preset aplicado e persistido por usuário; aplicado após recarregar o WebView
  sem ação do usuário; tema inválido rejeitado com `invalid_configuration` e o
  preset anterior intacto; tema de baixo contraste aceito com avisos;
  `reset` devolve o tema do VS (`#282828`); **0 exceções JS**, 0 chamadas pagas.
- Fora do escopo da 7a (fica para 7b/7c): painel de edição com preview ao
  vivo, aviso de contraste na UI, importar/exportar e a conferência de
  contraste dos temas embarcados.

## Fase 6 — fechamento (2026-11-16)

Aceitação consolidada na Exp (`25e762c6`) com o VSIX de HEAD (`a09a9ad`),
provedor loopback próprio e 0 chamadas pagas. Mapeamento para os ACs do plano:

- **AC — criar skill em `.lumen/skills/` e vê-la na UI sem reiniciar:**
  `projectSkillWithoutRestart` (source `.lumen/skills`, scope `Project`) e
  `removedAfterReload` (sumiu após remoção, sem reiniciar).
- **AC — skill acionada injeta o corpo; não acionada, não injeta:**
  `activationInjected` (`/code-review` injeta `[Skill: code-review]` + corpo),
  `automaticActivation` (casamento por descrição, sem `/nome` nem seleção
  manual) e `progressiveDisclosureNegative` (mensagem sem relação não injeta
  nada).
- **AC — desativar uma skill remove seu efeito imediatamente:**
  `disabledSkillRejected` — após desativar no painel, `/code-review` vira
  `skill_unavailable` com **0 chamadas ao provedor** e o autocomplete não
  sugere mais a skill; `reEnabled` reativa no painel.
- CNs do plano seguem cobertos pela suíte `SkillTests` (47, incluindo
  `name_mismatch`/`missing_frontmatter` como erros acionáveis, limite de
  8.192 chars no prompt e confinamento de `load_skill`) e pelos diagnósticos
  observados na Exp.
- Cleanup completo: fixture, credencial, conversas e estados desativados
  removidos; 0 exceções JS; `deploy-experimental.ps1 -Action Verify` confirma o
  VSIX empacotado. A fase fecha com todas as 8 tarefas do plano marcadas e a
  única falha conhecida da suíte sendo a ambiental pré-existente do `pwsh`.
- Commit único sugerido no plano não foi usado; a Fase 6 foi commitada por
  fatia (`dcdd97a` núcleo, `5c0e385` UI de Skills, `e720bdb`/`908dc86` exemplos
  e 6c, `452045c` 6b, `a09a9ad` 6d).

## Fase 6d — acionamento automático por descrição (2026-11-16)

## Fase 6d — acionamento automático por descrição (2026-11-16)

- `SkillCatalog.AutoActivate`: matching determinístico, sem discrição do modelo —
  a skill auto-ativa quando **duas ou mais palavras distintas da description**
  (tokens de 4+ caracteres, menos stopwords) aparecem na mensagem do usuário.
  Menção ao nome puro **não** auto-ativa: `/nome` e `load_skill` seguem sendo
  os únicos caminhos guiados por nome, preservando o progressive disclosure.
- `ByokSession.ActivatedSkills`: os automáticos entram depois dos explícitos e
  manuais, sem duplicar e sem estourar o limite de 8 — excedentes são
  pulados, nunca rejeitados. Skills desativadas ou fora do modo não estão na
  lista `available`, então não podem auto-ativar.
- Build Release: **0 erros, 0 avisos**. Suíte `.NET`: **1.596 aprovados** (47 de
  Skills, incluindo ordem de descoberta e não-duplicação) e a única falha é a
  ambiental pré-existente do `pwsh` AppExecutionAlias. `npm run check:ui`: 220 aprovados.
- Verificação na Exp (`25e762c6`) via `verify-skills-webview.mjs`: mensagem
  "please review this diff for security issues" injetou `[Skill: code-review]`
  com o corpo completo, sem `/nome` nem seleção manual; controles negativos —
  mensagem sem relação não injeta nada (`progressiveDisclosureNegative`),
  mensagem sem skill, `skill_unavailable` com 0 chamadas ao provedor
  após desativar. 0 exceções JavaScript, 0 chamadas pagas.
- Observação ambiental: um diretório de fixture órfão e vazio
  (`artifacts/context-49fe7fd8-…`) ficou preso por um handle de diretório de
  outro processo; cosmético, não bloqueia o ciclo (o `Prepare` só exige o manifest).

## Fase 6c — exemplos embarcados e verificação na Exp (2026-11-15)

- Três Skills de exemplo embarcadas no VSIX em `src/Lumen.Vsix/skills/` (`code-review`, `write-tests`, `explain-selection`), empacotadas via `<Content Include="skills/**/*">` e descobertas por `VisualStudioSkillSource` a partir da pasta `skills/` ao lado da assembly com `SkillScope.User` e source `"bundled/skills"`.
- Teste C# `SkillTests.BundledExampleSkillsAreValidAndMatchTheDocumentedSet` verifica que os 3 `SKILL.md` existem e passam no parser; controle negativo provado renomeando `name: code-review` para `wrong-name` (`SkillValidationException: name 'wrong-name' must match its folder name 'code-review'`).
- Build Release (`Lumen.sln`): **0 erros, 0 avisos** (`artifacts/phase6c.binlog`). Suíte `.NET`: **1.593 aprovados, 1 falha ambiental pré-existente** em `WorkspaceShellToolsTests.DetectedShellsExecuteOnlyAfterApproval` (`Win32Exception: Acesso negado` ao spawnar `pwsh.exe` via AppExecutionAlias da Microsoft Store a partir de `CreateProcessW`/`dotnet`). Os 44 testes de Skills e os 220 testes JS (`npm run check:ui`) passaram.
- Verificação na instância experimental real (`25e762c6`) via `scripts/verify-skills-webview.mjs` (WebView2 CDP + provedor loopback, sem chamadas pagas): confirmou as 3 skills bundled, recarga sem reiniciar o VS, criação de skill em `.lumen/skills/` sem reiniciar, controles negativos (`name_mismatch`, `missing_frontmatter` em `diagnostics`), injeção de `[Skill: code-review]` apenas com `/code-review`, rejeição `skill_unavailable` com 0 chamadas ao provedor após desativar, reativação persistida e remoção após recarga.
- Limitações: nenhuma; a UI da 6b foi verificada na continuação (STATUS 2026-11-15/16).

## Redesign da interface (2026-11-14)

- Interface de chat reorganizada com paleta verde, navegação lateral direta para provedores, histórico e ferramentas, e adaptação aos temas claro e escuro.
- Build sem avisos; 218 testes de interface passaram. A suíte .NET tem 1.591 testes; duas execuções completas nesta sessão falharam intermitentemente em `ShellExitAlsoKillsChildHoldingPipesOpen`, embora uma execução completa e duas isoladas tenham passado. A causa permanece não identificada.
- Chromium headless com host simulado exercitou navegação entre os quatro tópicos, foco, chat, aprovação, histórico, erro e tamanhos de janela variados, sem chamadas pagas. A rolagem da conversa, dos quatro painéis e da barra lateral foi verificada em 320×240, incluindo controle negativo. A nova interface ainda não foi verificada na instância experimental do Visual Studio.

## Fase 6b — painel e ativação de Skills (2026-11-14)

- A ponte lista Skills e diagnósticos, recarrega a descoberta e persiste nomes desativados em configurações locais; o host continua filtrando por modo e rejeita ativação manual ou por `/nome` de Skill desativada.
- O painel permite ativar/desativar e selecionar até oito Skills para o próximo envio; o compositor sugere nomes ao digitar `/`. Seleção e descrição não concedem permissões de ferramentas.
- Build sem avisos; 1.593 testes .NET e 220 testes de interface passaram. Controles negativos incluem Skill desativada, modo proibido e mensagens forjadas; recarga e persistência usam provedores simulados.
- Chromium headless com host simulado exercitou recarga, ativação/desativação, seleção, autocomplete e viewport estreito, sem chamadas pagas. A instância experimental foi verificada na continuação da 6c.
- Os comandos da UI para criar/editar Skills, abrir a pasta e desfazer alterações estão implementados; as operações são confinadas a `.lumen/skills/`, validam o nome e mantêm pilha de undo. Verificados na Exp com criação, edição, abertura e dois undos (edição e criação).

## Fase 6a — núcleo de Skills (2026-11-14)

- `SkillParser` (Lumen.Tools): frontmatter agentskills.io com subconjunto YAML
  (escalares simples/aspas/blocos `|` `>`, listas, mapa `metadata` de um
  nível); erros acionáveis com código e linha (`missing_frontmatter`,
  `duplicate_field`, `invalid_name`, `name_mismatch`, `description_too_long`,
  `invalid_modes`…). Restrição de modo via `metadata.lumen-modes`;
  `allowed-tools` é só informativo e **nunca** concede ferramenta/aprovação.
- `SkillDiscovery`: projeto `.lumen` > `.github` > `.claude` > `.agents`
  (`/skills`), depois usuário `~/.lumen`, `~/.copilot`, `~/.claude`,
  `~/.agents`. Primeira definição vence; cópias geram `duplicate_name`.
  Leitura via `WorkspaceGuard` da raiz de skills: junction para fora →
  `outside_allowed`.
- `SkillCatalog`: só nome+descrição no system prompt, limitado a
  `MaxPromptChars` (8192) com aviso de omissão; ferramenta readonly
  `load_skill {name, path?}` confinada à pasta da skill (`outside_skill`).
  Ativação explícita `/nome` (só se a skill existir) e manual pelo campo
  `skills` do payload `byok.chat` (≤8; `skill_unavailable` fora do modo).
  Descoberta refeita a cada envio — skill nova vale sem reiniciar o VS.
- VSIX: `VisualStudioSkillSource` (raiz da solução + perfil do usuário).
- Testes: `SkillTests` (41) cobre os CN do plano — sem frontmatter/nome
  duplicado → erro claro; 256 descrições máximas não passam de 8192 chars;
  skill `lumen-modes: plan` com `allowed-tools: create_file run_command` não
  anuncia escrita em Plan e `create_file` do modelo vira `unknown_tool`;
  junction de pasta e de raiz para fora rejeitadas; `../` em `load_skill`
  rejeitado. Controle negativo: remover o filtro de modo e o limite do prompt
  em `SkillCatalog` derruba 3 testes. Suíte completa: 1591/1591 verdes.
- Não verificado na Exp nesta fatia (UI de Skills é 6b; verificação Exp em 6c).
  Sem chamadas pagas.

## Fase 5d — ContextBuilder integral + AC multi-passos (2026-11-13)

- **5d-1 — @arquivos e contexto unificado.** `parseMentions` no compositor
  extrai `@arquivo` e `@solução`/`@solution` (deduplica por caminho
  normalizado; nunca casa e-mail inline). Cada menção é resolvida via
  `byok.context.capture` **antes** de criar o turno: buffer aberto preferido
  sobre disco, path canonicalizado e confinado ao workspace. Menção já
  anexada pelo painel não captura duas vezes; limites de contexto (8 itens /
  tamanho total) bloqueiam o envio localmente. Falha de captura bloqueia o
  envio, preserva o rascunho e nomeia o token com motivo localizado
  (`byokMentionFailed`, pt-BR/EN). O contexto entra na mesma mensagem User
  como segundo TextPart `[Context: label]`. System prompt por modo, seleção
  do editor e estrutura da solução já existiam (3c/5a); **skills ativas ficam
  para a Fase 6** (dependem do sistema de Skills, ainda não implementado).
- **5d-2 — AC de tarefa real multi-passos.** Estágio `agent-task` do
  verificador na Exp real com provedor loopback roteirizado: menção positiva
  injeta contexto do buffer aberto; menção traversal `../` é bloqueada sem
  POST (controle negativo); o modelo cria `Calculator.cs`,
  `CalculatorTests.cs` e um `Calculator.csproj` próprio e executa `run_build`
  aprovado — exit 0 prova que **ambos** os fontes compilaram juntos (o teste
  referencia `Calculator.Add(2, 3)`; CS0103 derrubaria o build se qualquer
  fonte faltasse); undo em lote restaura os 3 arquivos; colisão mid-task
  (`file_exists`) recupera sem duplicar escrita. **8 requests loopback,
  0 pagos, 0 exceções JS**. Orçamento 8/32768/16 necessário: 4 rodadas de
  tool + resposta = 5 rodadas; o padrão 4 aborta o build com
  `tool_budget_exceeded` (orçamento configurável funcionando como projetado).
- **Achado (não é bug do produto):** o `.csproj` legado da fixture vive em um
  buffer RDT somente-leitura do project system do VS cujo snapshot muda
  continuamente; `edit_file` corretamente recusa com `edit_conflict`, mesmo
  em solução recém-carregada. O verificador contorna criando um projeto
  dedicado. Curingas `*.cs`/SDK-style foram rejeitados: puxariam o
  `AcceptanceTests.cs` do estágio de aceitação e quebrariam o
  `BuildFixtureError`.
- Build Release **0 erros/avisos**; **1.550 C# / 217 JS** verdes, sem
  ignorados. Novos: teste C# create→edit→build realimentando cada resultado
  antes da próxima rodada (aprovação de terminal + cards Completed); 5 testes
  JS de menção (positivo, bloqueado sem POST, duplicata, `@solution`, limite).
  Evidência: `artifacts/phase5-agent-task-webview.json`.
- Fixture/credenciais/conversas próprias removidas, solução fechada sem
  salvar, Exp fechada e SHA-256 de todos os arquivos verificado em
  `Extensions\4hgcddl3.sps`. Nenhum perfil principal alterado; sem API paga.
- Lacunas: skills ativas dependem da Fase 6; a qualidade do plano/contexto
  depende do modelo; o estágio não reroda a regressão geral nem o layout
  203 px. Pendências do usuário: teste de symlink exige Developer
  Mode/elevação; passada manual de leitor de tela (NVDA/Narrator) feita pelo
  usuário e aprovada; decisão de merge do worktree paralelo
  `g-hangel-completar-fase-3`.
- Checkpoint commitado em `1acf6ac` e enviado ao remoto; evidências de teste
  e do verificador ficam locais em `artifacts/` (ignorado pelo git).

## Fase 5c — compactação, retry e paralelismo gated (2026-11-13)

- `ToolChatLoop.Compact`: estimativa de tamanho por caracteres (1 char ≈ 1
  token, inclui instrução do modo e specs das tools); limite
  `min(262144, ContextWindowTokens − outputTokens − 2048)`. Descarta turnos
  inteiros mais antigos (fronteira = mensagem User), preservando o prefixo
  system e o turno mais novo — nunca divide um par tool_call/tool_result.
  Roda pré-chat e no início de cada rodada. `ValidateInput` rejeita input
  atual oversized com `context_too_large` antes de capturar workspace, ler
  credenciais ou chamar o provedor.
- Host emite Notice `history_compacted` quando o pré-chat compacta (uma vez
  por turno); o loop emite durante o turno. Bridge aceita somente os códigos
  `history_compacted`/`retrying`; UI renderiza linha secundária localizada
  (pt-BR/EN) com anúncio `aria-live`; Notices não são persistidos no histórico.
- Retry: até 2 retentativas por rodada (3 tentativas), somente
  `rate_limited`/`provider_error` sem saída visível já emitida; respeita
  `Retry-After`, senão backoff 500 ms × 2^tentativa + jitter; Notice
  `retrying`. Ids de tool da tentativa falha são descartados do pareamento;
  tools já executadas não reexecutam. `network_error` e 401 não retentam.
- Paralelismo: `Task.WhenAll` somente com múltiplas chamadas + capacidade
  `ParallelTools` no modelo + todas as tools `ParallelSafe` (create_file,
  edit_file, apply_diff, run_command, run_build e run_tests excluídas).
  OpenRouter deriva a capacidade de `supported_parameters`. Resultados
  devolvidos na ordem das chamadas.
- Build Release **0 erros/avisos**; **1.549 C# / 212 JS** verdes, sem
  ignorados. Negativos: imagem atual de 300 mil chars rejeitada
  pré-credencial sem POST; 401 após tool não retenta nem reexecuta;
  compactação asserta a invariante de pares (nunca órfã); escritas/terminal
  nunca paralelizam; códigos de Notice desconhecidos rejeitados pelo bridge.
- Exp real (`verify-agent-tools-webview.mjs loop-resilience`, UI nova):
  20 turnos Ask em modelo de janela 8192 compactam com Notice e replay
  encolhido (primeiro POST com 2 mensagens, último entre 3 e 40); 429 com
  Retry-After 1 após tool → 3 requests, exatamente 1 execução de tool e id
  do resultado pareado com a chamada; `fixture/parallel` (com
  `parallel_tool_calls`) executa 2 leituras em paralelo e `fixture/auto`
  executa em sequência. **27 requests loopback, 0 pagos, 0 exceções JS**.
  Evidência: `artifacts/phase5-loop-resilience-webview.json`.
- Fixture/credenciais/conversas próprias removidas, solução fechada sem
  salvar, Exp fechada e SHA-256 de todos os arquivos verificado em
  `Extensions\f3dzgvrn.jkl`. Nenhum perfil principal alterado; sem API paga.
- Lacunas: estimativa de tokens é por caracteres, não tokenizer real; a
  sobreposição temporal do paralelismo é provada pelos testes C# (MaxActive),
  não pelo verificador; o estágio não reroda a regressão geral nem o layout
  203 px. ContextBuilder integral (@arquivos, skills, seleção) e o AC de
  tarefa real multi-passos seguem na fatia 5d.
- Checkpoint commitado em `629036c` e enviado ao remoto; evidências de teste
  e do verificador ficam locais em `artifacts/` (ignorado pelo git).

## Fase 5b — aplicar plano e orçamento configurável (2026-11-12)

- `ToolBudget` (domínio): rodadas 1–8, tokens de saída acumulados 1024–32768,
  invocações 1–32; padrão 4/8192/8 igual aos tetos anteriores. O loop usa o
  orçamento recebido; o check de continuação precede a execução da tool, então
  estourar rodadas/tokens não executa I/O adicional.
- Host: campo opcional `budget` no `byok.chat`, com shape exato e inteiros;
  fora da faixa ou malformado retorna `invalid_message` antes de capturar
  workspace, ler credenciais ou chamar o provedor. Bridge replica a validação.
- UI: painel "Orçamento do turno" com três inputs por envio (não persiste entre
  sessões); valor inválido desabilita o envio. Botão "Aplicar plano no modo
  Agent" aparece somente em Plan: muda para Agent, revoga consentimentos,
  anuncia via `aria-live` e foca o compositor — sem POST, sem apagar histórico.
- Build Release **0 erros/avisos**; **1.527 C# / 210 JS** verdes, sem ignorados.
  Negativos: budgets forjados (rodadas 99, tokens fracionários, campos extras,
  tipos errados) rejeitados sem POST; rodadas=1 interrompe o loop após 1 request
  com 0 tools executadas; orçamento de tokens/invocações customizado para o
  loop; botão ausente em Ask/Agent e inerte durante turno ativo.
- Exp real (`verify-agent-tools-webview.mjs plan-budget`, UI nova): turno Plan
  com prompt de critérios de aceite → aplicar plano sem requests → rodadas=1
  falha com `tool_budget_exceeded` após 1 request → dois budgets forjados via
  RPC direto rejeitados pelo host → envio bloqueado com rodadas=0 → orçamento
  2/2048/8 conclui na mesma conversa; layout 203 px sem overflow e mutação
  negativa detectada. **4 requests loopback, 0 pagos, 0 exceções JS**.
  Evidência: `artifacts/phase5-plan-budget-webview.json`.
- Fixture/credenciais/conversas próprias removidas, solução fechada sem salvar,
  Exp fechada e SHA-256 de todos os arquivos verificado em
  `Extensions\vhheq1gz.wvu`. Nenhum perfil principal alterado; sem API paga.
- Lacunas: orçamento é por envio (recarregar a UI restaura padrões); aplicar
  plano só troca o modo — não reenvia o plano automaticamente; a qualidade do
  plano depende do modelo. AC de tarefa real multi-passos, ContextBuilder
  integral, compactação, ParallelTools e retry seguem na fatia 5c.
- Checkpoint commitado em `6ae1092` e enviado ao remoto; evidências de teste
  e do verificador ficam locais em `artifacts/` (ignorado pelo git).

## Aceitação consolidada das Fases 3/4 (2026-11-12)

- **Causa raiz dos stalls silenciosos do verificador:** a Exp estava executando
  o build de **outro worktree** (branch `g-hangel-completar-fase-3`, implantado
  às 10:19), cuja UI usa um RPC assíncrono `byok.mode.set` com gate `modeReady`
  que corre contra o clique de envio do robô. O código deste worktree não tem
  esse RPC — o modo vai no payload de `byok.chat`. Evidência: o `byok.mjs`
  instalado era byte-idêntico ao do worktree Copilot e o wire log terminava em
  `byok.mode.set` sem `byok.chat` subsequente. Não é bug de produto deste branch.
- **Correção:** rebuild Release deste worktree (0 erros/avisos), desinstalação
  do pacote alheio e instalação do VSIX atual com SHA-256 de todos os arquivos
  em `Extensions\l2sbkly1.rv1`; fixture nova; reexecução integral.
- **Aceitação consolidada** (`verify-agent-tools-webview.mjs acceptance`):
  colar 1 MiB em **97 ms** com clamp 4096 e negativo 4097; escrita externa
  negada → permitida → undo; `dotnet test` real aprovado e reprovado pela UI;
  Error List populada com `#error` e negativo; cancelamento de stream em
  **623 ms** e de ferramenta em **95 ms** com processo morto (ESRCH) e sem
  sobrevivente; sequências de eventos contíguas; **12 requests loopback,
  0 pagos, 0 exceções JS**. Evidência: `artifacts/phase34-acceptance.json`.
- Regressão geral reexecutada no build atual: **37 requests loopback, 0 pagos,
  0 exceções JS**, todas as asserções funcionais e negativas verdes
  (`artifacts/agent-tools-webview.json`). Modos em Exp recém-aberta:
  **6 requests, 0 pagos** (`artifacts/phase5-modes-webview.json`). A primeira
  tentativa de modos após a regressão falhou apenas porque a UI reutilizada
  ficou em Agent; com Exp nova passou — o critério "Ask inicial" exige UI nova.
- Build Release **0 erros/avisos**; **1.505 C# / 207 JS** verdes.
- **Limitações registradas:** symlink de arquivo exige elevação/Developer Mode
  (`NewItemSymbolicLinkElevationRequired` reconfirmado em 2026-11-13 no
  shell não elevado: `New-Item -ItemType SymbolicLink` falha com
  `UnauthorizedAccessException`); o controle real não foi exercido —
  junction real e reparse points nos ancestrais passam.
  Passada manual de leitor de tela (NVDA/Narrator) feita pelo usuário e aprovada.
- **Nota de worktree paralelo:** o worktree Copilot
  (`~/.copilot/repos/copilot-worktrees/Extensão/g-hangel-effective-parakeet`)
  tem commits próprios (`af96a74`, `7349de1`, `6d2f418`) que implementam modos
  com `byok.mode.set` e declaram as fases 3/4 fechadas lá. Este branch
  implementa o mesmo escopo de forma diferente (modo no payload do chat) e as
  evidências acima são deste branch. A decisão de mesclar é do usuário;
  nenhum merge foi feito aqui.

## B9 — regressão readonly concluída (2026-11-11)

Uma execução integral na Exp com o pacote local da Fase 5 concluiu asserções
funcionais, captura PNG válida, 0 exceções JS e controle negativo que rejeitou
PNG corrompido. Foram 12 requests loopback, sem API paga; dados sintéticos
limpos, Exp fechada e pacote instalado verificado. Evidências:
`artifacts/auto-tools-webview.json` e `artifacts/auto-tools-webview.png`.
A causa do timeout CDP anterior não foi identificada: a conclusão descreve a
regressão atual, não garante ausência de intermitência em todos os ambientes.
O registro e o controle negativo estão no commit `8ab46d3`, já enviado ao remoto.

## Em andamento — Fase 5: seleção de modo (2026-11-11)

- Seletor Agent/Ask/Plan no chat, com Ask inicial. Trocar de modo limpa os
  consentimentos por envio; escrita/terminal só são habilitados em Agent.
  O host valida `mode` e rejeita flags de escrita/terminal em Ask ou Plan antes
  de capturar workspace, ler credenciais ou chamar o provedor. Sem `mode`, o
  padrão seguro é Ask. Ferramentas não anunciadas são rejeitadas no loop.
- Prompts de sistema distinguem Ask (responder e citar `arquivo:linha`), Plan
  (plano em fases com critérios de aceite, sem implementar) e Agent. O prompt
  é enviado mesmo sem ferramentas; modo ativo é gravado na conversa, sem apagar
  turnos anteriores ao trocar de modo. O painel separado de escrita manual é
  uma ação explícita independente do modo de ferramentas automáticas do chat.
- Build Release **0 erros/avisos**, **1.505 C# / 206 JS** verdes. Os controles
  negativos forjam flags e tool calls de escrita/terminal em Ask e Plan e
  verificam ausência de execução; troca de modo preserva a conversa.
- Exp: `verify-agent-tools-webview.mjs modes` usou provedor loopback: Agent
  editou buffer e desfez, Plan e Ask rejeitaram tool calls forjadas, histórico
  preservado e layout 203 px com mutação negativa detectada. **6 requests
  locais, 0 pagos e 0 exceções JS**. Evidência:
  `artifacts/phase5-modes-webview.json`. Fixtures/credenciais próprias limpas,
  Exp fechada, arquivos instalados verificados em `Extensions\hbngbmnp.22r`.
- Tentativas anteriores do verificador geral falharam por espera de checkbox
  desabilitado em Ask, declaração global duplicada no CDP e diário da fixture
  anterior ainda ativo; fixture própria foi limpa e a validação foi reduzida
  aos modos, sem reivindicar aprovação da regressão geral nessa execução.
- Aplicar plano e orçamento configurável foram concluídos na fatia 5b;
  compactação, retry e paralelismo gated na fatia 5c (acima). ContextBuilder
  integral e o AC de tarefa real multi-passos seguem pendentes.
  B9 foi encerrado posteriormente com regressão readonly completa; as
  alterações desta fatia de modos foram commitadas em `91e4ba5`.

## Registro anterior — snapshot e apply_diff (2026-11-11)

Implementação commitada em `dd826ea`: `get_edit_snapshot` devolve texto
integral e SHA-256 UTF-8 sem BOM de arquivo ou buffer MEF aberto, limitado a
65.536 caracteres JSON; resultado excessivo é recusado, não truncado. O consentimento
informa que o provedor pode receber buffers não salvos. `apply_diff` exige hash
correspondente e substituições exatas únicas não sobrepostas; usa aprovação de
escrita existente, diff visível e undo. Base obsoleta retorna `edit_conflict`.

Build Release 0 erros/avisos; **1.486 C# e 205 JS** passaram em execuções
separadas. Um teste C# de processo filho expirou na primeira execução paralela à
suite JS, mas passou isolado e na suite completa subsequente; causa não comprovada.
Na Exp, provedor loopback executou snapshot de buffer não salvo, patch e undo;
hash propositalmente incorreto foi rejeitado e disco preservado. Verificador
completo também validou aprovações, saída, consentimento e layout negativo:
**25 requests loopback, 0 pagos, 0 exceções JS**. Fixture e credenciais sintéticas
removidas, Exp fechada e SHA do pacote verificado. Evidências em
`artifacts/agent-tools-webview.json` e `artifacts/agent-tools-webview.png`.

Diagnósticos e descoberta de shells foram adicionados na continuação abaixo;
modos integrais permanecem na Fase 5. O usuário configurou `origin` e autorizou
o commit após concluir essas pendências.

## Diagnósticos e shells (2026-11-11)

- `get_errors` lê até 256 itens da Error List visível; arquivos externos são
  omitidos. O conjunto depende de filtros e pode estar desatualizado.
  `get_output_logs` captura a pane ativa, não todas as panes; saída acima do
  limite é recusada. Ambos podem conter informações sensíveis, explicitadas no
  aviso de consentimento antes do envio ao provedor.
- `detect_shells` procura cmd, PowerShell, pwsh e bash pelo PATH, com caminhos
  convencionais como fallback para pwsh e Git Bash; retorna apenas executáveis
  encontrados, sem executá-los. Teste de backend executou shells detectados após
  aprovação e comprovou negação antes da execução.
- `run_build`/`run_tests` usam `dotnet build/test` com `--no-restore`, alvo
  `.sln`, `.slnx` ou `.csproj` confinado ao workspace e aprovação de comando.
  O prompt mostra executável, argumentos e diretório; modo readonly e traversal
  são rejeitados. Podem executar código do projeto e acessar rede; não são uma
  integração universal com o Test Explorer. Processo continua em Job Object.
- Build Release 0 erros/avisos; **1.493 C# / 205 JS** verdes. Na Exp, com
  provedor loopback, snapshot/patch/undo e hash incorreto, diagnósticos, shell,
  negação de build/test e um build autorizado foram exercitados. O verificador
  confirmou código de saída do build, não que tenha sido zero. **33 requests
  loopback, 0 pagos, 0 exceções JS**; screenshot e controle negativo de layout.
  Não foi testado Error List populado/filtrado nem um teste .NET real aprovado
  pela UI; no backend build/test aprovado contra projeto inválido retorna exit
  não zero. Nenhuma cobertura universal de formatos de projeto é alegada.
- Limpeza apenas de dados próprios e Exp fechada. A Exp testou o pacote em
  `Extensions\le2lbosl.32w`. Após clean/build, só os metadados gerados
  `manifest.json` (`extensionDir` aleatório) e `catalog.json` diferiram; os
  demais arquivos coincidiram. O pacote limpo foi reinstalado e teve todos os
  arquivos verificados em `Extensions\n0a1c00s.vyu`; a UI não foi repetida
  após essa reinstalação. Evidências: `artifacts/agent-tools-webview.json` e
  `artifacts/agent-tools-webview.png`. B9 foi encerrado em uma execução posterior.

## Escrita pelo modelo e terminal aprovado — fatia 4d (2026-11-11)

- Opt-in separado por envio para escrita e terminal; ambos implicam leitura.
  Schema estrito e autoridade do host, nunca do modelo. Ask/Plan são recusados
  no backend; os modos completos ainda não estão expostos na UI.
- Escritas internas em disco/buffer aplicam automaticamente com diff inerte.
  Cada turno automático inicia um lote; manuais consecutivas compartilham lote.
  Undo restaura somente o sufixo mais recente, preserva anteriores e permite
  retomar restauração parcial. Conflitos impedem sobrescrever alterações alheias.
- Paths absolutos de drive permitem aprovação externa por operação; traversal,
  ADS, drive-relative, UNC/device permanecem rejeitados. Schema efetivo anuncia
  path 1.024 e conteúdo 65.536. Reserva de orçamento antecede escrita; commit
  seguido de cancelamento preserva resultado/diff e bloqueia a próxima I/O.
- Terminal: negar/permitir uma vez/permitir nesta sessão. Consentimento vinculado
  à geração da solução e revalidado após aprovação, antes de iniciar o processo.
  Argumentos escapados com oracle `CommandLineToArgvW`; processo suspenso entra
  no Job Object antes de executar. Fechamento do job encerra descendentes;
  timeout/cancelamento e drenagem de pipes são limitados, fora da UI thread.
  Resultado aparece no chat e no pane público `IVsOutputWindowPane` Lumen.
- Clean/build Release **0 erros/0 avisos**; **1.466 C# + 205 JS** verdes,
  sem ignorados. Negativos incluem autoridade/schema/path, orçamento antes de
  I/O, cancelamento após commit, undo/conflitos, escaping, callback stale,
  descendentes sobreviventes e getters COM/JSON proibidos no pacote.
- Exp real: manual + dois turnos automáticos, buffer não salvo/disco separados,
  diff e três undos isolados passaram. Negar e permitir terminal uma vez passaram.
  A inspeção Output falhou por acesso PowerShell ao RCW; helper C# `DTE2`
  confirmou pane por nome/GUID e `exit 0`/stdout. Retomada usou o endpoint
  loopback já configurado, sem repetir os turnos de escrita/undo.
- Retomada: cancelamento em aprovação, consentimento persistente/revogado ao
  reabrir, readonly sem escrita/terminal, aprovação externa sem opção de sessão,
  HTML inerte, arquivo sem chave sintética, solução fechada sem POST e layout
  203 px passaram. Mutação `min-width:240px` foi detectada e desfeita.
  **11 requests loopback na retomada, 0 pagos, 0 exceções JS**; screenshot salvo.
  O relatório explicita a retomada, não é uma nova execução integral única.
- Configuração/credencial/conversas removidas pelos IDs próprios; fixture limpa,
  Exp fechada e SHA de todos os arquivos instalados confirmado em
  `Extensions\xont1ooq.0ro`. Nenhum perfil principal/servidor alheio alterado.
- Evidências: `artifacts/agent-tools.binlog`, `artifacts/tests/agent-tools.trx`,
  `artifacts/agent-tools-ui-tests.log`, `artifacts/agent-tools-webview.json`,
  `artifacts/agent-tools-webview.png`. B9 foi encerrado em uma execução posterior.
- Lacunas: `apply_diff`, diagnósticos, detecção completa de shells e modos
  integrais. UI real exercitou cmd e negação externa, não uma escrita externa
  aprovada nem todos os shells; demais cenários são testes de backend.
  Não valida disco cheio, leitor de tela ou transporte de API paga.

## Escrita manual interna — fatia 4c (2026-11-10)

Registro histórico; limitações superadas na 4d estão descritas acima.

- Painel explícito de `create_file`/`edit_file`, Apply, diff plaintext e undo de
  todas as escritas manuais pendentes na sessão nativa. Não chama provedor,
  lê credenciais ou envia conteúdo ao modelo; não habilita escrita automática.
- Arquivo aberto: edição MEF sem salvar/recarregar, transação nativa e marcador
  `ITextUndoPrimitive` não mesclável. Undo compara conteúdo, versão reiterada,
  identidade de buffer/solução e estado do marcador; transação externa invalida
  o lote, mesmo quando a pessoa restaura o mesmo texto. Native Undo bloqueia o
  lote enquanto desfeito; Native Redo pode restabelecer a identidade esperada.
- Arquivo fechado: preparação em background e substituição atômica. Imediatamente
  antes da escrita/restauração/exclusão, solução e RDT são revalidados sem yield
  na UI thread; documento aberto durante a preparação é recusado. Journal
  preserva bytes exatos e recusa mudanças externas. Undo parcial remove somente
  registros restaurados. Cancelamento tardio não transforma commit em falha.
- Limites: path relativo até 1.024 caracteres, conteúdo até 65.536, 64 escritas;
  texto próprio de buffer até 1 Mi caracteres e journal de disco até 16 MiB.
  Textos, delegates e referências auxiliares são liberados ao remover registros;
  o marcador nativo conserva só estado e o vínculo da transação. Diff limitado,
  com finais de linha e truncamento explícitos. Não existe persistência do lote.
- Clean/build Release **0 erros/avisos**, **1.405 C# + 197 JS**, sem falhas ou
  ignorados. Negativos incluem aprovação/backend, path/reparse, schema/autoridade,
  conflito, orçamento, cancelamento, commit do host e metadados do VSIX.
- Pacote definitivo na Exp passou Apply/Undo em disco+buffer não salvo (duas
  edições consecutivas no mesmo buffer), Undo/Redo nativo, HTML inerte, alteração
  externa, texto idêntico após edição posterior, paths, solução fechada e 203 px
  sem overflow; **0 pagos / 0 exceções JS**. Baseline offline, recuperação,
  negativo de layout e regressão readonly manual completa também passaram.
- Regressão readonly automática chegou ao fim dos controles funcionais, mas
  expirou na captura CDP antes do relatório final/asserção de exceções JS (B9).
  Captura isolada também expirou; nenhum turno foi repetido naquele checkpoint.
  A regressão foi concluída posteriormente (B9 em `BLOQUEIOS.md`). Provedor/chave/conversas sintéticos próprios
  removidos; fixtures limpas, Exp fechada, preview próprio 4177 parado e SHA de
  todos os arquivos instalados confirmado. Sem commit/push.
- Falhas corrigidas com evidência: identidade JSON net48/netstandard separada
  (argumentos extraídos como strings em Tools); `VsUndoHistory.UndoStack` não
  suportado (`LastUndoTransaction` também é stub, `CanUndo` sempre true), logo
  substituído pelo marcador. DTE normaliza newline em algumas leituras; só o
  verificador normaliza comparações. Fake concorrente passou a coletar respostas
  em fila thread-safe, sem relaxar a asserção de cancelamento. Verificação de
  recarga offline aguarda `Page.loadEventFired` antes de avaliar o novo contexto,
  após falha real de avaliação destruída pela navegação; sem retry/polling.
- Evidências: `artifacts/write-slice.binlog`, `artifacts/tests/write-slice.trx`,
  `artifacts/write-tools-webview.json` e `artifacts/write-tools-webview.png`.
- Lacunas: sem `apply_diff`, undo de turno Agent, escrita pelo modelo, painel de
  terminal, aprovação externa ou modos completos. Testes de corrida no commit
  usam gate sintético; não injetam concorrência no editor da Exp. Não valida
  falha/disco cheio no SO real nem leitor de tela. Fases 4/5 seguem abertas.

## Invocação automática readonly (2026-11-09)

- Opt-in por envio (`readTools: true`) anuncia apenas `read_file`, `list_dir`,
  `find_files` e `grep`. Ausente/false não captura nem executa ferramentas.
  Checkbox inicia desmarcado e perde consentimento após envio/troca de modelo.
  A UI avisa sobre conteúdo de disco, custo potencial e persistência local
  plaintext; o painel de leitura manual não alimenta o chat.
- Solução capturada antes de credenciais/HTTP, validada antes/depois do I/O.
  Execução Ask, em sequência e somente após argumentos completos + ToolUse.
  Escrita, terminal e autoridade enviada pelo modelo continuam bloqueados;
  rejeições retornam erro correlacionado ao modelo sem chamar o executor.
- Tetos: quatro requests, oito invocações, 8.192 tokens de saída solicitados,
  dois minutos por turno; stream/resultados/input também limitados. Sem retry,
  reexecução ou compactação silenciosa. System prompt trata arquivos como dados
  não confiáveis. Estado assinado preservado; chave filtrada antes de UI/arquivo.
- Arquivo mantém pares usuário/assistente; replay expande calls/resultados em
  mensagens assistant/tool/assistant. Histórico reaberto mostra resultados
  inertes e estados; turns falhos/cancelados e chamadas legadas órfãs não são
  reenviados. Rejeição prévia aparece como Failed, nunca como execução concedida.
- Clean/build Release **0 erros/0 avisos**; **1.310 testes C# + 179 JS** verdes,
  sem ignorados. Controles negativos de ferramentas desconhecidas/escrita,
  argumentos, orçamento, stream incompleto, segredos, captura, cancelamento e
  gate; UI rejeita Running/Completed de ferramenta desconhecida, mas aceita
  Failed correlacionado sem I/O.
- WebView2 real + provedor loopback: leitura automática das quatro ferramentas,
  reinjeção, disco diferente do buffer não salvo, HTML inerte, reabertura/replay,
  opt-out, traversal/escrita negados, limite de loop e cancelamento durante
  continuação com recuperação. Solução fechada impediu POST; hash do arquivo
  inalterado; 203 px sem overflow. **12 requests locais, 0 pagos, 0 exceções JS**.
  Regressão `verify-webview` passou com negativo de overflow forçado.
- Cleanup removeu somente provedor/chave/conversas sintéticos por IDs do
  manifesto; solução fechada sem salvar, pasta/manifesto e preview próprio 4177
  removidos/encerrados. Exp fechada; pacote final SHA-256 confirmado em
  `Extensions\2sfwep3y.aeq`. Servidores preexistentes 4173/4175 intocados.
  Nenhum commit, remote ou push neste trabalho.
- **Lacunas:** teste real usa transporte OpenRouter compatível com OpenAI;
  Anthropic/Gemini e assinaturas têm testes fake, não API paga real. Troca de
  solução durante I/O e cancelamento de leitura longa não foram corridos na Exp
  (cancelamento real foi durante POST seguinte). Symlink de arquivo indisponível;
  acessibilidade estrutural, sem passada manual de leitor de tela. Fases 4/5
  não concluídas; orçamento fixo, contexto conservador sem tokenizer/compactação.

Evidências: `artifacts/auto-tools.binlog`, `artifacts/tests/auto-tools.trx`,
`artifacts/auto-tools-webview.json`, `artifacts/auto-tools-webview.png` e
`artifacts/webview-verification.json`. Reprodução em `COMANDOS.md`.

## Evidências verificadas da fatia 4b (2026-11-09)

Registro histórico antes da antecipação readonly automática acima.

- Formulário explícito para `read_file`, `list_dir`, `find_files` e `grep`, via
  RPC `byok.tool.read`, sem provedor/modelo selecionado e sem gasto de tokens.
  Resultados ficam em plaintext; não são anexados ao chat/contexto nem enviados
  ao modelo. Leitura usa disco, não buffers não salvos.
- Host captura a solução aberta na UI thread, executa em background com
  `ChatMode.Ask` e revalida raiz/caminho da solução ao terminar. O navegador não
  escolhe raiz, modo ou aprovação. Escrita e terminal continuam indisponíveis.
- A Exp revelou `MissingMethodException` em `ToolInvocation.get_Arguments()`:
  referências de `System.Text.Json` divergiam entre net48 (10.0.0.14) e
  netstandard (10.0.0.0). O verificador de identidade do pacote rejeitou esse
  pacote real antes da correção. `Lumen.Tools` foi alinhado a netstandard2.0;
  adaptadores de solução/editor permanecem no host net48, sem binding shims.
- Build Release **0 erros/0 avisos**; **1.261 testes C# + 166 JS** verdes, sem
  ignorados. RPC testa argumentos/autoridade forjados, limites, correlação,
  cancelamento determinístico e recuperação do gate, sem HTTP/cofre/settings.
- WebView2 real: quatro ferramentas, range do disco diferente do buffer não
  salvo, truncamento explícito e formulário sem modelo confirmados. Traversal,
  ADS, binário, arquivo acima de 1 MiB, escrita/comando e raiz/modo forjados
  rejeitados. Hash do arquivo inalterado; HTML malicioso permaneceu texto inerte;
  recuperação após erro e largura de 203 px sem overflow; **0 exceções JS**.
  Solução fechada retornou `workspace_unavailable`. Regressão `verify-webview`
  passou, incluindo offline local e controle negativo de overflow forçado.
- Instalação final verificada por SHA-256 em `Extensions\3udphwea.yqv`. Solução
  sintética fechada sem salvar, fixture/manifesto removidos, Exp e preview 4176
  próprios encerrados; servidores preexistentes 4173/4175 intocados. Nenhuma API
  paga/probe, alteração de configuração/credencial ou criação de conversa.
- **Limites:** troca de solução durante I/O e cancelamento de I/O longo não foram
  corridos na Exp; cancelamento foi testado com executor determinístico e
  transportes JS fake. Symlink de arquivo continua indisponível por permissão.
  Acessibilidade é estrutural, sem passada manual de NVDA/Narrator. Invocação
  automática pelo modelo e realimentação pertencem à Fase 5. Fase 4 não concluída.

Evidências: `artifacts/phase4b.binlog`, `artifacts/tests/phase4b.trx`,
`artifacts/phase4b-runtime-negative.xml`, `artifacts/read-tools-verify.json`,
`artifacts/read-tools-closed.json`, `artifacts/read-tools-webview.png` e
`artifacts/webview-verification.json`. Reprodução em `COMANDOS.md`.

## Base de backend da fatia 4a (2026-11-09)

Registro histórico anterior à integração 4b; TFM e estado de host/UI foram
atualizados na seção acima.

- `Lumen.Tools` net48 incluído na solução e no VSIX. Os mesmos fontes são
  compilados por link nos testes net10, sem dependência de assemblies do VS.
- `WorkspaceGuard`: caminhos Windows canônicos, fronteira raiz+separador,
  traversal/ADS/UNC/devices/aliases proibidos, reparse points rejeitados nos
  ancestrais. Leitura valida a identidade do handle real antes de ler conteúdo.
  Destino de escrita externo é apenas validado: não concede autorização nem escreve.
- `ApprovalBroker`: Ask/Plan negam escrita e comandos antes de I/O/prompt;
  escrita interna autorizada para o futuro executor com undo; externa pede
  confirmação a cada operação; comandos permitem opt-in restrito à sessão.
  Aprovação explícita não é dispensada por opt-in; cancelamento/dispose não
  concedem consentimento tardio. Prompts serializados, argumentos copiados.
- Executor somente leitura: `read_file` com linhas, `list_dir`, `find_files`
  com glob e `grep` literal case-sensitive. Schemas publicados são usados pela
  validação; campos duplicados/desconhecidos e tools não registradas são recusados.
  UTF-8/UTF-16 estritos, arquivos até 1 MiB, busca até 4 MiB/1.024 entradas/32 níveis,
  até 256 resultados e saída JSON até 65.536 caracteres. Truncamento explícito;
  busca informa arquivos binários/grandes ignorados. Todo trabalho em background.
- Build Release **0 erros/0 avisos**; **1.244 testes C# + 160 JS** verdes.
  Controles negativos com junction real, handle real de identidade errada e
  fora da raiz (handle fechado), políticas negadas, argumentos inválidos,
  limites e cancelamento. **GAP:** symlink de arquivo não pôde ser criado por
  permissão do Windows; teste registra a lacuna, sem alegar esse controle real.
- Evidências: `artifacts/phase4a.binlog`, `artifacts/tests/phase4a.trx`.
  Nenhuma API paga, alteração em providers/credenciais ou processo VS iniciado.
  **Não verificado na Exp, não integrado ao chat, não commitado.** Prompts são
  interface com fake nos testes; não existe ainda confirmação real na UI,
  editor/undo, escrita, shell, blocklist ou diagnóstico. Fase 4 não concluída.

## Backend de write-tools (2026-11-09)

- `WriteToolCatalog`: specs `create_file` e `edit_file` com `additionalProperties:
  false`, `path` e `content` string (1–4096 e 0–1.048.576 chars), `required`
  ambos. Validação reutiliza `ReadToolCatalog.ValidateText` (rejeita null e
  surrogate órfão); campos duplicados/desconhecidos, tipo não-string e
  limite fora do intervalo são recusados antes de qualquer I/O.
- `WorkspaceWriteTools`: executor puro `Lumen.Tools` sem host/UI.
  - `create_file`: falha se o arquivo já existe (sem sobrescrever); grava UTF-8
    sem BOM atomicamente (temp sibling + `File.Move`/`File.Replace`); cancelamento
    nunca toca o destino.
  - `edit_file`: lê bytes anteriores, valida UTF-8/UTF-16 estrito (rejeita
    binário com `binary_file`), grava conteúdo novo atomicamente, produz diff
    LCS limitado (64 mudanças, 96 chars/linha, 1.024 linhas; `truncated` só
    quando algo foi descartado).
  - Guarda de bytes: conteúdo que passa o limite de chars mas excede 1 MiB em
    UTF-8 (multibyte) é rejeitado com `file_too_large` — arquivo recém-escrito
    deve permanecer dentro do cap de leitura.
  - `EditJournal`: undo por turno em ordem reversa; restaura bytes exatos
    anteriores (preserva encoding e BOM originais); `create_file` remove o
    arquivo criado, `edit_file` restaura o conteúdo anterior.
  - `ApprovalBroker.AuthorizeWriteAsync`: Ask/Plan negam antes de I/O
    (`mode_denied`); escrita dentro do workspace autoriza sem prompt; fora
    pede confirmação (`approval_denied` se recusado); nenhum opt-in dispensa
    aprovação explícita.
- Build do projeto de testes Release **0 erros/0 avisos**; **21 testes de
  write-tools verdes** + 123 testes de ApprovalBroker/ReadTools/Guard verdes
  (sem regressão). Controles negativos: modo negado antes de prompt, arquivo
  existente não sobrescrito, binário não corrompido, traversal rejeitado,
  cancelamento não deixa arquivo, multibyte >1 MiB rejeitado, arquivo >1 MiB
  no disco rejeitado na edição, diff truncado marcado corretamente, undo
  restaura bytes exatos de arquivo UTF-16 LE com BOM.
- **Não verificado na Exp, não integrado ao chat, não commitado.** Aprovação
  usa fake nos testes; não existe ainda confirmação real na UI, editor buffer
  sync, terminal, blocklist ou diagnóstico. Fase 4 não concluída.
- **Limites:** build e testes limitados ao projeto de testes (memória host
  crítica ~0,8 GB); full solution build + suite completa + verificação na Exp
  diferidos para quando a memória liberar.

## Backend de shell (2026-11-09)

- `CommandToolCatalog`: spec `run_command` com `command` (string 1–4096),
  `args` (array de strings, 0–256), `cwd` (string opcional 0–4096),
  `timeout_ms` (integer 1–60000, default 30000). `required`: `command` e
  `args`. `additionalProperties: false`. Validação rejeita tipo não-string
  em args, array não-array, número não-inteiro, campos duplicados/desconhecidos
  e usa `ReadToolCatalog.ValidateText` (null/surrogate órfão).
- `CommandBlocklist`: defesa em profundidade (AGENTS.md §5). Bloqueia antes
  do prompt de aprovação: `format`/`shutdown`/`diskpart`/`cipher`
  incondicional; `rm -rf /` e variantes contra raiz; `del`/`erase`/`rd`/
  `rmdir` com `/s` + `/q`/`/f`; `git push --force`/`-f`; `git reset --hard`;
  `git clean -fd`/`-f -d`; `cmd /c` com `format`/`shutdown`/`del /s`/`rd /s`
  embutido.
- `WorkspaceShellTools`: executor puro `Lumen.Tools` sem host/UI.
  - `ProcessStartInfo` com `UseShellExecute: false`, `CreateNoWindow: true`,
    argumentos escapados individualmente (convenção `CommandLineToArgvW`:
    aspas duplas, backslash antes de aspa dobrado, backslash final dobrado).
    Nunca concatena string do modelo em linha de comando (AGENTS.md §5).
  - Captura stdout+stderr com `RedirectStandard*`, codificação UTF-8, limite
    65.536 chars cada, truncamento explícito. Exit code retornado no resultado.
  - Timeout mata o processo e retorna `command_timeout`; cancelamento mata o
    processo antes de propagar `OperationCanceledException`. Read tasks
    param de ler no cancelamento (não lançam).
  - `cwd` confinado ao workspace via `WorkspaceGuard.Resolve`; se ausente,
    usa a raiz do workspace.
  - Todo comando exige aprovação via `ApprovalBroker.AuthorizeCommandAsync`
  (AGENTS.md §1). Ask/Plan negam antes do prompt. Agent permite com
  opt-in "sempre nesta sessão" (`AllowCommandsForSession`).
- Build do projeto de testes Release **0 erros/0 avisos**; **38 testes de
  shell verdes** + 120 testes de Write/Approval/Guard verdes (sem regressão).
  Controles negativos: blocklist bloqueia antes do prompt (12 padrões),
  modo negado, aprovação negada, timeout mata processo, cancelamento mata
  processo, `cwd` fora rejeitado, args inválidos, tool desconhecida,
  escape de espaços/aspas/backslash, `cmd /c format` bloqueado,
  `git status` não bloqueado.
- **Não verificado na Exp, não integrado ao chat, não commitado.** Não
  existe ainda confirmação real na UI, Output Window pane, buffer do editor
  ou diagnóstico. Fase 4 não concluída.
- **Limites:** mesmo que o slice anterior — build/testes limitados ao projeto
  de testes; full solution build + suite + Exp diferidos (memória host
  crítica). `Process.Kill` não mata processos filhos em netstandard2.0;
  testes evitam `cmd /c` que cria subprocessos.

## Registro histórico — adaptadores/RPC de write + shell (2026-11-09)

Estado anterior à fatia 4c: os detalhes abaixo não descrevem o painel manual
atual. Não havia painel de terminal, aprovação real ou teste Exp de escrita.
Os limites, a semântica do lote e o reload foram substituídos na 4c.

- **Interfaces:** `IWriteToolExecutor` (CaptureAsync, ExecuteAsync,
  UndoTurnAsync, PendingChanges) e `IShellToolExecutor` (CaptureAsync,
  ExecuteAsync) em `Lumen.Core.Domain`.
- **RPC:** `ByokSession` agora aceita `IWriteToolExecutor?` e
  `IShellToolExecutor?` opcionais. Novos handlers: `byok.tool.write`
  (`create_file`/`edit_file`, arguments até 1 MiB+4K), `byok.tool.write.undo`
  (undo em lote do turno) e `byok.tool.command` (`run_command`, arguments
  até 8K). Result limitado a 65.536 chars. Todos validam tool name, JSON
  object, tamanho e correlação de ID antes/depois do I/O.
- **Adaptadores VS:** `VisualStudioWriteTools` e `VisualStudioShellTools`
  seguem o padrão de `VisualStudioReadTools`: capturam raiz/solução na UI
  thread, validam antes/depois, executam em background com `ChatMode.Agent`
  (escrita) e `ChatMode.Agent` (shell). `EditJournal` é compartilhado entre
  chamadas no mesmo turno (capturado em `CaptureAsync`) para undo em lote.
- **Buffer sync:** após gravar no disco, `VisualStudioWriteTools` recarrega
  o buffer do editor via `IVsRunningDocumentTable.FindAndLockDocument` +
  `IVsPersistDocData.ReloadDocData` (sem fechar/reabrir, preserva cursor).
  `IntPtr` é convertido com `Marshal.GetObjectForIUnknown` e liberado com
  `Marshal.Release`.
- **Aprovação:** ambos adaptadores implementam `IApprovalPrompt.RequestAsync`
  retornando `ApprovalResponse.Deny` por agora. Escritas dentro do workspace
  funcionam (Agent permite sem prompt); escrita fora e comandos são negados.
  A UI de aprovação real ainda não existe — é o próximo passo.
- Build VSIX Release **0 erros/0 avisos**, VSIX gerado. Build do projeto de
  testes **0 erros/0 avisos**. **99 testes** (ByokSession + Write + Shell +
  Approval) verdes, sem regressão.
- **Não verificado na Exp, não commitado.** Buffer sync não testado com
  documento aberto real. Aprovação real na UI pendente. Ferramentas de
  diagnóstico (`get_errors`, `run_build`) não implementadas. Full solution
  build + suite completa + Exp diferidos (memória host crítica ~1,3 GB).

## Evidências verificadas da fatia 3d (2026-11-09)

- **Acessibilidade estrutural verificada no WebView2 real:** skip link
  (`Pular para o campo de mensagem`) que foca o composer, `aria-keyshortcuts`
  `Control+Enter` no composer e no enviar, região de anúncio `aria-live="polite"`
  `aria-atomic="true"`, histórico com `role="list"`, `:focus-visible`,
  `forced-colors` e `prefers-reduced-motion`. O verificador estendido confirmou
  esses atributos no DOM da Exp e o foco real do botão de erro (`byok-url`).
- **Estados de erro acionáveis:** `invalid_key`/`permission_denied` focam a chave;
  `invalid_url`/`unsafe_url` focam a URL; o botão de erro abre o painel e move o
  foco (verificado: `panelOpen:true`, `activeId:byok-url`). `429` não é acionável
  e mostra o tempo de espera.
- **Contrato `Retry-After` de ponta a ponta:** o host faz GET com até 3 tentativas
  em 429 (POST nunca é repetido); o fixture devolve `Retry-After` 0, 0, 90 e a Exp
  surfaced **`retryAfter:90`** com a mensagem "aguardar 90 s" em pt-BR,
  `rateLimitAttempts:3` e `credentialValid:true`. O wire carrega segundos inteiros
  (`Math.Ceiling`), `null` quando o provedor não envia o header; o validador JS
  aceita `null`/inteiro≥0 e rejeita negativo, fracionário, string, booleano e NaN.
- **Controles negativos provados:** a mutação que desativa o retry fez o teste
  `Catalog429SurfacesTheLastResponseRetryAfterNotTheFirst` falhar
  (`Expected:3, Actual:1`) antes de reverter; o validador JS rejeita `retryAfter`
  inválido; mensagem forjada sem modelo continua `invalid_message`; layout
  forçado ainda é detectado. O teste de contraste recusa accent de baixo contraste.
- **Verificação final:** clean/build Release **0 erros/0 avisos**, **1.121 testes
  C# + 160 JS** verdes. Regressão completa na Exp: `verify-webview`,
  `verify-byok` (acessibilidade/erro/429), histórico prepare→restart→resume→cleanup
  (3 turnos, replay 3 mensagens, exclusão confirmada) e contexto (buffer não salvo,
  seleção `Sample.cs:2-2`, traversal/tamanho/binário/stale rejeitados). Todos os
  relatórios com **0 exceções JS, 0 probes, 0 serviços pagos**.
- **Instalação final:** SHA-256 de todos os arquivos em `Extensions\nyyefehw.l21`,
  revalidado após reinício e ao fechar. Exp e servidor de fixtures próprios
  encerrados; preview 4173 intocado. Conversas/configurações/credenciais sintéticas
  removidas por IDs do manifesto; as **três configurações reais** (Xkiro, ChatGPT,
  gemini) e seus modelos preservados; cache de probes `{"version":1,"entries":[]}`.
  Nenhum commit, remote ou push da 3d.
- **Limites honestos:** a suportação a screen reader é estrutural (ARIA/live
  region conferidos no DOM), não uma passada manual com NVDA/Narrator; o contraste
  é verificado pelos tokens/teste (≥3:1 foco, ≥4,5:1 texto), não medido à mão.
  Navegação por teclado exercitada nos atalhos/foco verificáveis; uma passada
  assistiva completa fica para revisão manual do usuário.

Evidências: `artifacts/phase3d.binlog`, `artifacts/tests/phase3d.trx`,
`artifacts/byok-webview.json`, `artifacts/byok-webview.png`,
`artifacts/history-webview.json`, `artifacts/history-webview.png`,
`artifacts/context-webview.json`, `artifacts/context-webview.png`,
`artifacts/webview-verification.json`. Reprodução em `COMANDOS.md`.

## Evidências verificadas da fatia 3c (2026-11-08)

- **Captura somente por ação explícita:** `@arquivo` / `@file`, `@solução` /
  `@solution`, seleção e Output, com prévia plaintext e remoção antes de Enviar.
  Caminho vazio captura o documento ativo. Buffer aberto não salvo prevalece
  sobre disco; arquivo aberto ilegível não cai silenciosamente para disco.
- **Snapshots nativos imutáveis:** UI envia apenas GUIDs, não texto forjado;
  máximo 8 anexos, 65.536 caracteres cada e 131.072 no total. Inventário da
  solução contém nomes relativos, não conteúdo de todos os arquivos (256 arquivos,
  orçamento de 1.024 nós/nomes). Não há coleta automática nem ferramentas executáveis.
- **Confinamento:** raiz da solução carregada, paths canônicos, rejeição de
  traversal, ADS/UNC/device paths e reparse points; disco valida também o handle
  aberto antes de leitura. Mudança/fechamento da solução rejeita anexos pendentes
  com `context_stale` antes de credenciais/HTTP. Histórico existente não recaptura
  contexto. Cancelamento/erro retém IDs para retry; resultado do chat os consome.
- **Persistência:** prompt e contexto são `TextPart`s separados; `contextText`
  opcional na página de histórico, sem alterar o schema de mensagens antigas.
  Contexto arquivado é replayado sem leitura do editor. Chave selecionada no
  prompt/contexto é bloqueada antes de HTTP, e eco de credencial não persiste.
- **Verificação final:** clean/build Release com **0 erros/avisos**, **1.115 testes
  C# + 151 JS** verdes. Controles negativos cobrem schema/GUIDs, stale, orçamento,
  cancelamento, Unicode inválido, traversal/ADS e junction real. O teste de symlink
  de arquivo não pôde criar o link por permissão; esse controle específico não
  foi exercitado (não é evidência de aprovação de symlink de arquivo).
- **VS/WebView2 reais:** arquivo não salvo (disco permaneceu com marcador antigo),
  arquivo ativo com caminho vazio, seleção `Sample.cs:2-2`, inventário e Output;
  UI de referência/remoção e envio; hash do contexto no wire igual ao arquivo
  persistido e à página de histórico. Traversal, arquivo >65.536, binário e
  fechamento da solução foram rejeitados; captura e stale não mudaram contadores
  HTTP. **0 probes, 0 serviços pagos e 0 exceções JS** na verificação final.
- **Ajustes demonstrados na Exp:** seleção por text manager/MEF ficou indisponível
  após Output; produto usa `DTE.ActiveDocument` / `Document.Selection`. Fixture
  usa DTE2 público em helper C# e lê seu manifesto como UTF-8; expressão de
  screenshot isolada evita declarações globais entre reexecuções do verificador.
- **Instalação final:** todos os arquivos comparados por SHA-256 em
  `Extensions\azw1lxof.mmi`. Exp fechada; fixtures/configuração/credencial/conversas
  próprias removidas. As três configurações originais e seus modelos foram preservados.
  Nenhum commit, remote ou push da 3c; 3d concluída em 2026-11-09 (ver acima).

Evidências: `artifacts/phase3c.binlog`, `artifacts/tests/phase3c.trx`,
`artifacts/phase3c-js.log`, `artifacts/context-webview.json`,
`artifacts/context-webview.png`, `artifacts/webview-verification.json` e
`artifacts/byok-webview.json`. Reprodução em `COMANDOS.md`.

**Limites:** histórico continua JSON local não criptografado. Prévia pode conter
texto sensível capturado explicitamente; revisar/remover antes de Enviar. Limite
por caracteres não garante encaixe na janela de tokens do modelo (Fase 5).
Snapshots não são atualizados automaticamente; novo rascunho mantém anexos.
DTE usa margem conservadora de CRLF, podendo rejeitar perto do limite sem truncar.

## Evidências verificadas da fatia 3b (2026-11-08)

- **Arquivo de conversas pelo host:** `%LocalAppData%\Lumen\conversations\<guid>.json`,
  versão 1, gravação temporária + substituição atômica. Preserva imagens,
  raciocínio e estados assinados/opacos sem convertê-los para texto da UI.
  Títulos, pares user/assistant, estados, GUIDs, Unicode e arquivos são validados;
  reparse points de diretório/ancestrais são recusados. Teste de symlink de arquivo
  não disponível neste Windows, sem alegar cobertura real desse caso.
- Busca por título e texto das mensagens, abrir, renomear, nova conversa e
  exclusão com confirmação explícita. Abrir/buscar não consultam credenciais
  nem fazem HTTP; o arquivo continua legível mesmo sem o provedor configurado.
  A mesma seleção mantém a conversa; selecionar outro modelo inicia contexto novo
  sem excluir arquivos anteriores.
- Cancelamentos/falhas ficam visíveis e persistidos, mas seus pares são excluídos
  do replay. Ecos da credencial, inclusive com escapes JSON, impedem a gravação.
  Argumentos de tool incompleta ficam como parte opaca; nenhuma tool é executada.
- **Virtualização medida:** páginas de até 10 turnos, cache de 40 turnos na UI,
  DOM limitado à janela visível com overscan. No WebView2 real, 500 turnos com
  alturas distintas (147–656 px na amostra) montaram **4 itens no início / 8 no
  fim**, alcançando o turno 500. Expandir raciocínio não o recolapsa ao medir.
  Testes JS percorrem os 500 turnos e verificam a expulsão da quinta página do cache.
- **Reinício real da Exp:** conversa concluída + cancelada voltou do disco;
  selecionar o mesmo provedor/modelo preservou o histórico. Continuação explícita
  ficou com 3 turnos salvos e enviou **3 mensagens**, não 5: o par cancelado não
  foi reenviado. Busca/abrir/renomear/nova conversa/excluir não aumentaram os
  contadores HTTP das fixtures. Exclusão sem confirmação foi rejeitada e o
  verificador de layout detectou sobreposição deliberada antes do resultado verde.
- Build limpo Release **0 erros / 0 avisos**, **982 testes C# / 121 testes JS**,
  sem falhas ou ignorados. Evidências: `artifacts/phase3b.binlog`,
  `artifacts/tests/phase3b.trx`, `artifacts/history-webview.json`.
  `verify-webview.mjs`, `verify-byok-webview.mjs` e as etapas prepare/resume/cleanup
  de `verify-history-webview.mjs` passaram; relatórios reais sem exceções JS.
- Install SHA-256 em `Extensions\1tzvqmde.asw`, verificado após reinício e ao
  fechar a Exp. Fixtures e credenciais sintéticas removidas por IDs próprios;
  as três configurações reais do usuário foram preservadas. Sem APIs pagas,
  remote, push ou alterações ao perfil principal. As alterações da 3b estão locais,
  ainda sem novo commit.
- **Limites explícitos:** arquivo até 64 MiB / 10.000 turnos, listagem até 256
  conversas, página serializada até 4 Mi caracteres (erro controlado, sem truncar).
  Conversas ficam em JSON local **não criptografado**, avisado na UI; chaves
  continuam no cofre nativo. Anexos mostram aviso, sem preview. Tokens de uso não
  são parte persistida. Busca lê os arquivos locais completos; paginação limita o
  tráfego C#↔JS e a memória/DOM do navegador, não a leitura interna do host.
  Em alturas pequenas, a página precisa de rolagem vertical para acomodar composer
  e painéis; acessibilidade final permanece na 3d.

## Evidências verificadas da fatia 3a (2026-11-07)

- **Chat unificado:** o spike de eco e o histórico de 500 mensagens fake foram
  removidos; o composer envia ao provedor real selecionado no painel BYOK, que
  virou apenas configurações. Estado vazio, linha de modelo ativo, contador
  0/4096, log limitado a 20 turnos (persistência fica para 3b).
- Opções de esforço/imagem e a ajuda com aviso de custo vivem em `<details>`
  colapsados; a nota de conexão desaparece quando conectado. Composer caiu de
  751 px para **380 px** em 203×422 (chat 120 px mínimo) e para **278 px** em
  375/900, sem overflow/sobreposição nos quatro layouts; overflow forçado ainda
  é detectado.
- Code block streamed recebe botão **copiar** com clipboard real verificado no
  WebView2 (`Copiado`, aria-label localizado). Sem foco do SO o `writeText`
  falha com `NotAllowedError` mesmo com permissão concedida; o verificador usa
  `Page.bringToFront` + `Emulation.setFocusEmulationEnabled` e o produto tem
  fallback `execCommand` com feedback explícito de falha (teste JS cobre).
- Build Release **0 erros / 0 avisos**; **813 testes C#** e **93 testes JS**
  verdes, sem ignorados. Evidências: `artifacts/phase3a.binlog`,
  `artifacts/tests/phase3a.trx`.
- Install verificado por SHA-256 em `Extensions\5avnx5ua.xdx`, revalidado após
  fechar o VS. `verify-webview.mjs` e `verify-byok-webview.mjs` exit 0, ambos
  com `exceptions: []` e `layoutNegative: true`.
- BYOK no chat unificado: catálogo com 3 modelos fixture, chave sintética
  limpa do campo, seleção sem visão bloqueia anexo, com visão anexa PNG real;
  chat com imagem + streaming terminou com texto e card de tool (nenhuma
  ferramenta executada). Probe sem consentimento recusado sem POST; duas
  tentativas da mesma capability = **1 POST** inconclusivo; cancelamento em
  **10 ms** com orçamento reconciliado 3/5. 401/404/429 com erros distintos;
  429 preservou credencial e configuração. Submit forjado sem modelo não cria
  turno (controle negativo no WebView2 real).
- Orçamento de probe é por sessão nativa: a primeira execução pós-falha exigiu
  reiniciar a Exp para voltar a 5/5. O servidor de fixtures contou 4 POSTs de
  probe no total; o verificador prova que a seção de probe gastou exatamente
  **1** (deduplicação + clique sem consentimento + consentimento recusado não
  gastam) e o cancelamento reconciliou o orçamento nativo. Todos os POSTs
  foram para o servidor loopback.
- **Sem chamadas a serviços reais de IA e sem gasto.** As três configurações
  BYOK reais que o usuário criou na Exp (Xkiro, ChatGPT, gemini) foram
  preservadas; o verificador removeu apenas as configurações sintéticas que
  criou. Cache de probes terminou `{"version":1,"entries":[]}`.
- Exp fechada e servidor de fixtures encerrados ao final. Perfil principal do
  VS intocado.

## Evidências verificadas da Fase 2 (2026-11-07)

- Build limpo Release: **0 erros / 0 avisos**; **813 testes C#** e **91 testes
  JavaScript** aprovados, sem falhas ou testes ignorados, com controles negativos.
  Evidências: `artifacts/phase2.binlog` e `artifacts/tests/phase2.trx`.
- Pacote instalado somente na Exp `18.0_25e762c6Exp`, em
  `Extensions\gjhfvx4a.sor`; todos os arquivos empacotados conferidos por SHA-256
  após instalar e novamente após fechar o VS.
- Ponte C# e WebView2 reais, usando apenas fixtures loopback: catálogo público
  OpenRouter com validade da chave desconhecida, chave sintética limpa do campo,
  seleção sem visão bloqueando imagem/effort e seleção com visão liberando anexo
  real e esforços Low/High. Chat com imagem e streaming terminou com texto e
  card de tool; nenhuma ferramenta foi executada.
- Probe sem consentimento foi recusado sem POST. Duas tentativas da mesma
  capacidade produziram **um único POST**, resultado inconclusivo e orçamento
  nativo 4/5. Cancelar outro probe já recebido pelo servidor local respondeu em
  **7 ms**, com erro `cancelled` e orçamento reconciliado em 3/5, sem reembolso.
- 401 e 404 exibiram erros distintos sem salvar configuração nem anunciar sucesso
  de listagem. 429 autenticado preservou validade da credencial e configuração;
  o catálogo público não foi usado como prova de chave válida.
- BYOK sem overflow em larguras 203/375/900; overflow deliberadamente forçado foi
  detectado. Verificador legado revalidou eco real, 500 mensagens virtualizadas,
  markdown/highlight/diff e recuperação após mensagens inválidas. Reload offline
  carregou **12 assets**, todos de `https://lumen.local`; layouts 203/320/375/900
  sem overflow ou sobreposição. Ambos os relatórios têm `exceptions: []` e
  `layoutNegative: true`.
- A reexecução detectou uma corrida de inicialização do verificador, corrigida
  esperando o handshake real antes de acessar o DOM. Os dois verificadores
  passaram depois da correção (R15).
- Exp fechada e servidores iniciados por esta sessão encerrados. O preview
  preexistente na porta 4173 foi mantido. Configurações sintéticas removidas:
  `%LocalAppData%\Lumen\providers.json` contém `[]` e o cache de probes contém
  `{"version":1,"entries":[]}`. Perfil principal do VS intocado.
- **Sem chamadas a serviços reais de IA, sem credenciais reais e sem gasto pelo
  agente.** O teste com chave real foi feito pelo próprio usuário na UI.
- **Aceite do usuário (2026-11-07, na conversa):** "não precisa fazer o ollama
  local, eu já fiz os testes e o chat realmente funciona" e "vamos para a próxima
  fase". B7 dispensado explicitamente; B8 aceito como limitação (deployments
  manuais). Fase 2 fechada e checkpoint commitado com esses limites registrados.

## Evidências da Fase 1 (2026-11-07)

- Tipos criados em `src/Lumen.Core/Domain/`: `ContentPart` (text/image/toolCall/
  toolResult/reasoning/unknown), `Message`, `Conversation`, `ModelInfo`,
  `ModelCapabilities`, `CapabilityOrigin`, `CapabilitySource`, `Confidence`,
  `ReasoningSupport`, `ReasoningKind`, `ReasoningIntent`, `StreamEvent` (8 casos),
  `ToolSpec`, `ToolInvocation`, `ToolResult`, `ChatModePolicy`,
  `ConversationSerializer`.
- `dotnet clean` + build Release: **0 erros / 0 avisos** (`artifacts/phase1.binlog`).
- **97 testes C#** verdes (62 novos) e **17 testes JS** verdes.
- `CoreReferencesTests` prova que `Lumen.Core` não referencia `Microsoft.VisualStudio*`,
  `Microsoft.Web.WebView2*`, `System.Net.Http`, `System.Net.Sockets` nem WPF, e que
  o TFM é `.NETStandard,Version=v2.0`. O detector tem controle negativo próprio.
- Round-trip de conversa preserva imagem base64, argumentos de tool call,
  `ProviderState` opaco de raciocínio e duas serializações consecutivas são idênticas.
- `kind` desconhecido vira `UnknownPart` com o JSON bruto intacto — o teste afirma
  explicitamente que **não** vira `TextPart`.
- 19 casos de JSON malformado/truncado/inválido retornam `false` + `invalid_json`
  sem lançar exceção, incluindo surrogate isolado e nesting acima de `MaxDepth` 64.
- Matriz de permissões coberta nos 12 pares modo×categoria: Ask e Plan negam
  escrita e comando; Agent permite escrita dentro do workspace com undo e exige
  aprovação para escrita fora e para **qualquer** comando.
- **Regressão de UI verificada após o rebuild:** install com SHA-256 em
  `Extensions\cmbvfewn.ha2`, **Exibir → Lumen** abriu, `verify-webview.mjs` exit 0
  com 10 requests todos em `https://lumen.local`, `exceptions: []`,
  `layoutNegative: true`, eco real funcionando. VS fechado em seguida.

## Evidências concluídas da Fase 0

- Probe `net48` + WPF no SDK .NET 11 RC: zero erros/avisos, 49,12 s.
- Build **limpo** Release: zero erros/avisos, VSIX gerado em
  `src/Lumen.Vsix/bin/Release/net48/Lumen.Vsix.vsix`.
- **35 testes C#** e **17 testes JavaScript** passaram, com controles negativos.
- Instalação/desinstalação somente na Exp `18.0_25e762c6Exp`: exit 0 e
  comparação SHA-256 de todos os arquivos instaláveis.
- Exibir → Lumen abriu a tool window no VS Community 18.12 Insiders.
  Foi necessário atualizar o cache com `/RootSuffix Exp /UpdateConfiguration`;
  o script de deploy agora faz isso automaticamente.
- WebView2 real: handshake C#, eco multilinha, markdown, highlight C#, diff
  com duas colunas, 500 mensagens virtualizadas; botão Final alcançou a nº 500.
- Composer cresceu de 40 para 120 px. Layouts emulados no WebView2
  203×422, 320×320, 375×700 e 900×900 ficaram sem overflow horizontal/sobreposição.
  O controle negativo forçou largura mínima incorreta e foi detectado.
- Mensagem inválida e Unicode isolado foram rejeitados pelo C# com
  `invalid_message`; ping posterior confirmou recuperação sem crash.
- Reload com cache desabilitado e rede offline carregou 10 assets, todos de
  `https://lumen.local`, e completou handshake real. Sem exceções JS observadas.
- Troca **Escuro → Claro no menu do VS** alterou o fundo de `#282828` para
  `#F9F9F9`, com `theme.changed`, mesmo `performance.timeOrigin`, sem reload.
  Essa evidência é da hive **anterior ao reset** e não foi repetida: depende de
  troca manual de tema, não de estado da hive. Todo o resto foi revalidado.
- Cópia com range `[99.0,)` recusada pelo instalador real (exit 2003).
  Verificador de instalação rejeitou asset adulterado.
- Revisão detectou Unicode inválido aceito pelo parser mas rejeitado ao
  decodificar string; os testes falharam antes e passaram após a correção.
- **Reset da hive validado (B6).** `CreateExpInstance.exe /Reset
  /VSInstance=18.0_25e762c6 /RootSuffix=Exp` reduziu a hive de 25 entradas/156
  arquivos para 1/6, sem nenhum arquivo "lumen". Controle negativo:
  `/VSInstance=18.0` deu exit **0** e resetou a hive errada, deixando a real
  intacta com Lumen instalado — a verificação por contagem de arquivos detectou.
- **Ciclo completo re-executado na hive resetada:** build Release 0 erros/0 avisos,
  35 testes C# + 17 JS verdes, install verificado por SHA-256 em
  `Extensions\nrqagayt.p4k`, **Exibir → Lumen** abriu a tool window,
  `verify-webview.mjs` exit 0 com os 10 requests em `https://lumen.local`,
  `exceptions: []` e `layoutNegative: true`, e `Verify` exit 0 após fechar o VS.
- **Controle negativo do detector de menu:** com Lumen desinstalado,
  `scripts/open-experimental.ps1 -Action Open` falhou com exit 1 e
  `The Lumen command is missing from the Exibir menu`; após reinstalar, exit 0.

## Evidências locais (ignoradas pelo Git)

- `artifacts/phase0.binlog`, `artifacts/phase1.binlog`, `artifacts/phase2.binlog`,
  `artifacts/phase3a.binlog`, `artifacts/phase3b.binlog`, `artifacts/phase3c.binlog`,
  `artifacts/phase3d.binlog`, `artifacts/net48-probe.binlog`.
- `artifacts/tests/phase0.trx`, `artifacts/tests/phase2.trx`,
  `artifacts/tests/phase3a.trx`, `artifacts/tests/phase3b.trx`,
  `artifacts/tests/phase3c.trx`, `artifacts/tests/phase3d.trx`.
- `artifacts/webview-verification.json`, `artifacts/webview-offline.png`.
- `artifacts/webview-theme.json`, `artifacts/byok-webview.json`,
  `artifacts/byok-webview.png`, `artifacts/history-webview.json`,
  `artifacts/history-webview.png`, `artifacts/context-webview.json`,
  `artifacts/context-webview.png`.
- `artifacts/vs-activity.xml`, `artifacts/vs-update.xml` e logs `lumen-*.log`.

O preview em navegador nunca simula conexão C#: sem host, enviar fica
indisponível. Teste de produto foi feito no WebView2 do VS, não apenas no preview.
Nenhum serviço real de IA, credencial real ou crédito pago foi utilizado.

Estado ao fim da Fase 0: hive `18.0_25e762c6Exp` resetada, Lumen em
`Extensions\nrqagayt.p4k`, Exp fechada e servidores da sessão encerrados.
O estado atual da Fase 2 está registrado nas evidências acima; o perfil
principal do VS permanece intocado.

## Próximo passo exato

**Fase 5 fechada pela fatia 5d (2026-11-13); Fase 6 não iniciada.**
ContextBuilder integral (@arquivos + contexto unificado) e o AC de tarefa real
multi-passos foram verificados na Exp com loopback (acima). Todos os demais
critérios da Fase 5 já estavam verificados nas fatias 5a–5c; skills ativas
ficam para a Fase 6 por dependerem do sistema de Skills. Próxima etapa:
**Fase 6 — Skills** (formato agentskills.io, discovery, progressive disclosure,
acionamento e UI), na ordem de `PLANO.md`. O remoto `origin` foi configurado
pelo usuário, que autorizou commits e pushes. Atenção ao worktree paralelo
`g-hangel-completar-fase-3` (ver nota acima) antes de qualquer merge.

**Limite de gasto:** nenhuma chamada real a APIs pagas sem chave de teste
fornecida pelo usuário **e** autorização por escrito. Probing ativo continua
opt-in, orçado e só sobre o modelo selecionado. A Fase 3 não toca APIs pagas.

Fase 1 commitada em `316b796`; Fase 2 commitada com os limites aceitos acima.
A revisão da Fase 2 gerou controles negativos para isolamento de credenciais,
GUIDs canônicos, replay de requests pagos, EOF prematuro e preservação de blocos
assinados. Probe HTTP usa um único POST mínimo, consentimento explícito e cinco
verificações por sessão; HTTP 200 sem evidência semântica é inconclusivo, não
suporte confirmado. Rejeições condicionais ou de `tool_choice` não justificam
negativo permanente. Remover configuração invalida seu cache sem devolver o
orçamento da sessão.

O registry embarcado tem cobertura limitada. O host permite substituição por
`%LocalAppData%\Lumen\capabilities.registry.json`, validado pelo resolver, sem
buscar atualização na rede. Overrides manuais ainda são apenas da sessão.

## Decisões e limites

Lumen é chat próprio independente, VSSDK clássico `net48` + WPF/WebView2.
Aprovações, ausência de dependência de Copilot e política de credenciais continuam
as de `AGENTS.md`. A autorização desta sessão cobriu comandos locais, caches e o
reset da hive **experimental**; nunca cobriu alterar o perfil principal do VS,
criar remote, dar push ou usar APIs pagas.

Para retomar: `AGENTS.md` → `PLANO.md` → `ARQUITETURA.md` → `PESQUISA.md` →
este arquivo e `BLOQUEIOS.md`; comandos reproduzíveis em `COMANDOS.md`.
