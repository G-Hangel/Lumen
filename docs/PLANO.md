# PLANO — Lumen

Plano executável para o agente que vai construir a extensão inteira.
Leia `AGENTS.md` antes. Consulte `ARQUITETURA.md` e `PESQUISA.md` como referência.

## Como usar

- Execute as fases **na ordem**. Cada fase tem critério de aceite verificável.
- Uma fase só termina quando: build limpo + testes verdes + **controle negativo
  rodado** + commit + `docs/STATUS.md` atualizado.
- A **Fase 0 é bloqueante**: ela existe para resolver empiricamente as
  incógnitas do SDK. Não comece feature antes de ela passar.
- Se bloquear: registre em `docs/BLOQUEIOS.md` com evidência (comando, saída,
  `arquivo:linha`) e siga para a tarefa independente seguinte. Não invente.
- Ao resolver um `A CONFIRMAR` da arquitetura, **atualize o documento** com a
  resposta e a fonte. O plano é vivo.

Legenda: `[ ]` pendente · `[x]` feito · `AC` critério de aceite ·
`CN` controle negativo (provar que o verificador rejeita o caso ruim).

---

## Fase 0 — Spike de toolchain e UI (BLOQUEANTE)

**Objetivo:** provar o ciclo completo *restore → build → instalar → abrir no VS →
WebView2 renderizando o chat* antes de investir em qualquer feature. Resolve os
`A CONFIRMAR` de `ARQUITETURA.md` §2.2.

Caminho escolhido: **VSSDK clássico, `net48`, WPF + WebView2**
(`ARQUITETURA.md` §2). Os pacotes vêm do nuget.org — **não dependem do workload**.

### Tarefas

- [x] Confirmar versões NuGet compatíveis e suas fontes: SDK 17.14.40265,
      BuildTools 18.5.38461, WebView2 1.0.4314.35, ReferenceAssemblies 1.0.3.
      Fallback 17.14.2120 não foi necessário (`PESQUISA.md` §2.7).
- [x] Probe `net48` + WPF com SDK .NET 11 RC: zero erros/avisos, 49,12 s.
      Não foi necessário instalar outro SDK.
- [x] Criar somente `Lumen.Vsix`, `Lumen.Core` e `Lumen.Tests` na solução.
- [x] Manifesto clássico Community `[18.0,)`, arquitetura amd64.
- [x] `LumenToolWindow : ToolWindowPane` aberta por **Exibir → Lumen** na Exp.
- [x] WebView2 com `UserDataFolder` explícito em `%LocalAppData%\Lumen\WebView2`.
- [x] Assets locais empacotados, mapping `https://lumen.local/`, sem CDN.
- [x] Ponte C#↔JS versionada, tipada e com ids de correlação; eco real validado.
- [x] Spike com markdown, highlight C#, diff lado a lado, 500 mensagens
      virtualizadas e composer expansível, usando bibliotecas locais.
- [x] Troca real Escuro → Claro no VS atualizou CSS sem recarregar o documento.
- [x] `Directory.Build.props`, analyzers e runner de testes configurados.
- [x] Documentar todos os comandos **testados**, incluindo reset da hive.
      Build/install/uninstall/verify/abertura/fechamento/cache/reset estão em
      `docs/COMANDOS.md`. B6 resolvido com autorização do usuário em 2026-11-07.

### AC

- `dotnet build -c Release` produz um `.vsix` sem erro e sem aviso.
- O `.vsix` instala na instância 18.12 Insiders sem erro.
- `devenv /RootSuffix Exp` abre e a tool window Lumen aparece.
- **O WebView2 renderiza** (não fica em branco) e o spike mostra markdown +
  highlight + diff + 500 mensagens com rolagem fluida.
- Trocar o tema do VS muda o chat sem reiniciar.
- `docs/COMANDOS.md` tem os comandos exatos e testados.
- Todos os `A CONFIRMAR` de `ARQUITETURA.md` §2.2 resolvidos e escritos.

### CN

- Instalar o VSIX numa versão fora do range deve ser **recusado** pelo
  VSIXInstaller (prova que o `InstallationTarget` está efetivo).
- Reinstalar mesma `Id`+`Version`: **neste VSIXInstaller 18.12, atualiza/repara**
  (verificado em 2026-11-07; corrige a hipótese de que seria ignorado).
  O script desinstala antes, atualiza o cache experimental e compara SHA-256;
  uma cópia com asset adulterado deve ser rejeitada pelo verificador.
- Mensagem JS malformada enviada ao C# → **rejeitada com erro controlado**,
  não exceção não tratada nem crash do VS.
- Abrir a tool window **sem rede** → a UI carrega igual (prova que não há CDN).
- `CreateExpInstance /Reset /VSInstance=18.0` retorna exit **0** mas reseta a
  hive errada e deixa a real intacta — verificar contagem de arquivos, não o
  exit code. O reset válido usa `/VSInstance=18.0_25e762c6`.
- `open-experimental.ps1 -Action Open` com Lumen **desinstalado** → exit 1 e
  `The Lumen command is missing from the Exibir menu`, encerrando o VS que abriu.

### Commit

`feat: bootstrap Lumen VSIX with verified toolchain and WebView2 chat spike`

---

## Fase 1 — Núcleo de domínio

**Objetivo:** tipos puros, sem VS e sem HTTP, que sustentam todo o resto.

### Tarefas

- [x] `Conversation`, `Message`, `MessageRole`, `ContentPart`
      (text / image / tool-call / tool-result / reasoning) em
      `src/Lumen.Core/Domain/`. `ReasoningPart` guarda `ProviderState` opaco para
      replay (`PESQUISA.md` §1.9). `UnknownPart` preserva `kind` desconhecido.
- [x] `ModelInfo`, `ModelCapabilities` (flags de `ARQUITETURA.md` §2.9),
      `ReasoningSupport`, `CapabilitySource`, `Confidence`, `ReasoningKind`,
      `CapabilityOrigin` (origem **por** capacidade, exigida pelo DoD global 2).
- [x] `ReasoningIntent = Off | Minimal | Low | Medium | High | Max`.
- [x] `StreamEvent` como união discriminada por `StreamEventKind`:
      `TextDelta, ReasoningDelta, ToolCallStart, ToolCallArgsDelta, ToolCallEnd,
      Usage, Done, StreamError` (`PESQUISA.md` §1.9).
- [x] `ToolSpec` / `ToolResult` / `ToolInvocation` com JSON schema; nome de tool
      restrito a `^[a-zA-Z0-9_-]{1,64}$`.
- [x] `ChatMode = Agent | Ask | Plan` e a matriz de permissões de
      `ARQUITETURA.md` §2.8 em `ChatModePolicy` (função pura, aplicável no
      ApprovalBroker e não só na UI).
- [x] Serialização round-trip de conversa em JSON (`ConversationSerializer`),
      com `TryParse` que retorna código de erro em vez de lançar.
- [x] `Lumen.Tests` com xUnit: **97 testes** verdes, sendo 62 novos da Fase 1.

### AC

- `Lumen.Core` compila sem referenciar **nenhuma** assembly do VS ou HTTP.
  Verificado por `CoreReferencesTests`, que inspeciona as referências reais da
  assembly compilada (VS, WebView2, HTTP, sockets, WPF) e o TFM `netstandard2.0`.
- Round-trip de serialização preserva todos os tipos de `ContentPart`,
  incluindo imagem base64 e argumentos de tool call. Verificado, mais a
  estabilidade de duas serializações consecutivas.
- Testes verdes: **97** aprovados, 0 falhas.

### CN

- JSON de conversa **malformado / truncado** deve falhar de forma controlada,
  não corromper o histórico nem lançar exceção não tratada.
  Coberto por 19 casos (truncado, `null`, array no lugar de objeto, role/enum
  inválido, base64 inválido, media type não-imagem, argumentos não-objeto,
  parts vazio, surrogate isolado, nesting acima de `MaxDepth` 64): todos retornam
  `false` + `invalid_json`, sem exceção.
- `ContentPart` desconhecido deve ser preservado ou descartado explicitamente —
  nunca silenciosamente virar texto. Vira `UnknownPart` com o `kind` e o JSON
  bruto intactos, e re-serializa byte a byte; o teste afirma `IsNotType<TextPart>`.
- Controle negativo adicional: `DeserializedJsonElementsOutliveTheirBackingDocument`
  falha com `ObjectDisposedException` se o `.Clone()` dos argumentos for removido
  (verificado revertendo a correção: 1 falha).

### Commit

`feat: add Lumen core domain model and stream event contract`

---

## Fase 2 — BYOK: provedores, descoberta e capacidades

**Objetivo:** colar uma chave e ver a lista de modelos com badges corretos de
visão / raciocínio / tools. **Esta é a feature-assinatura do produto.**

### Tarefas

- [x] `IChatProvider` + `IModelCatalog` + `ICredentialStore`.
- [x] Adapters implementados e exercitados com mocks, nesta ordem:
      1. **OpenAI-compatível** (cobre OpenAI, DeepSeek, Groq, Mistral, xAI,
         Together, Cerebras, vLLM, llama.cpp, LM Studio) — base URL configurável.
      2. **OpenRouter** — metadado mais rico, melhor para validar a UI.
      3. **Anthropic** — `capabilities` nativo.
      4. **Gemini**.
      5. **Ollama** — local, sem chave, ótimo para testes sem custo.
      6. **Azure OpenAI** — deployments, não model ids.
- [x] Descoberta de modelos exatamente por `PESQUISA.md` §1.1–1.5.
      Listagem autenticada valida a chave sem geração; o catálogo público do
      OpenRouter mantém validade **desconhecida**, inclusive em 401/402/429.
      Exceção aceita: Azure usa nomes de deployments informados; inventory
      automático adiado (B8, aceito pelo usuário em 2026-11-07).
- [x] `CapabilityResolver` em camadas (`PESQUISA.md` §1.6):
      nativo → registry → heurística → probe opt-in. Heurística de visão não
      substitui evidência nativa; nome não inventa controle wire de raciocínio.
- [x] `capabilities.registry.json` embarcado, substituível por JSON local validado
      sem release. Cada entrada com `source`, `confidence`, `verifiedAt`.
- [x] Mapeamento de `ReasoningIntent` por provedor (`PESQUISA.md` §1.7`),
      **omitindo o campo** quando não suportado.
- [x] Streaming normalizado para `StreamEvent` (`PESQUISA.md` §1.9`),
      incluindo remontagem de tool-call deltas e preservação de
      `signature` / `thoughtSignature` para replay; EOF prematuro não confirma resposta.
- [x] Entrada de visão nos três formatos (`PESQUISA.md` §1.8`), com validação
      de tamanho antes de enviar.
- [x] `ICredentialStore` via **Windows Credential Manager**
      (`PESQUISA.md` §1.10`), fallback DPAPI; testes com chaves sintéticas.
- [x] Tratamento de erro por código HTTP conforme a tabela de §1.10,
      com backoff + jitter honrando `retry-after`; POST pago nunca é repetido automaticamente.
- [x] UI de configuração: adicionar provedor, colar chave, testar, listar
      modelos com badges e **origem da capacidade** visível.
- [x] Fixtures SSE/NDJSON para cada provedor em `Lumen.Tests/Fixtures/`.

**Fase concluída com limites aceitos pelo usuário (2026-11-07).** O usuário
testou com chave real na UI e confirmou que a listagem e o chat funcionam;
dispensou explicitamente o AC de Ollama local (B7) e aceitou o inventory Azure
manual (B8) ao autorizar o avanço para a Fase 3. O agente não fez nenhuma chamada
paga: toda a verificação automatizada usou mocks/fixtures e WebView2 real com
fixture loopback. Probe mínimo não confirma suporte por HTTP 200: retorna
inconclusivo, sem chamada extra. Consentimento, limite cinco, deduplicação,
cache negativo conservador e cancelamento foram exercitados com fixtures.

### AC

- Com uma chave válida, a lista de modelos aparece com badges de visão,
      raciocínio e tools.
- Com Ollama local (sem chave, sem custo), o fluxo completo funciona:
      listar → selecionar → conversar com streaming → tool call.
- A origem de cada capacidade é exibida (`Native` / `Registry` / `Heuristic` /
      `Probe` / `UserOverride`).
- Nenhuma chamada paga acontece sem ação explícita do usuário.
- A chave **não aparece** em nenhum log, arquivo de settings, ou mensagem de
  erro — verificado por teste.
- Testes verdes usando mocks e fixtures, sem rede.

### CN

- **Chave inválida (401)** → rejeitada com mensagem de chave.
  **Base URL errada (404)** → mensagem de URL, **não** de chave.
  **429** → chave aceita como válida, entra em backoff. Três casos, três
  comportamentos distintos, cada um com teste.
- Um modelo **sem visão** no registry deve ser corretamente marcado como
  `Vision = false`, e o anexo de imagem deve ficar **desabilitado** para ele.
- Um modelo que **não suporta** raciocínio deve receber a requisição **sem**
  campo de effort — teste asserta a ausência do campo no payload.
- Stream com **evento desconhecido** no meio e com **JSON truncado** no fim:
  o parser não pode quebrar nem perder o texto já recebido.
- Tool call com `arguments` partido em 5 deltas deve remontar exatamente igual
  ao JSON original.
- Teste de redação: procurar a chave em logs/settings/erros deve retornar
  **zero** ocorrências.

### Commit

`feat: add BYOK providers with layered model capability discovery`

---

## Fase 3 — UI do chat

**Objetivo:** chat usável de verdade em WebView2, com a ponte C#↔JS da Fase 0.

### Tarefas

- [x] Protocolo de mensagens tipado e versionado entre C# e JS, com ids de
      correlação para pedido/resposta. Validar **toda** mensagem recebida
      (version 1, validação de envelope/payload nos dois lados; negativos de
      schema, id desconhecido e payload gigante exercitados na Exp).
- [x] Layout: histórico rolável virtualizado, composer multilinha, barra de modo
      (Agent/Ask/Plan), seletor de modelo, seletor de effort (500 turnos
      virtualizados medidos; modos verificados em 2026-11-11/12).
- [x] Renderização incremental de streaming — aplicar delta no DOM, **não**
      re-renderizar a mensagem inteira a cada chunk.
- [x] Markdown (markdown-it, `html:false`) + code block com highlight
      (highlight.js) e botão copiar (3a; clipboard real verificado).
- [x] Bloco de **raciocínio colapsável**, separado da resposta.
- [x] **Tool cards**: nome, argumentos, estado (pendente/aprovando/executando/
      concluído/falhou), resultado expansível, diff embutido (4b–4d).
- [x] Anexo de imagem (drag-drop, colar, arquivo) — habilitado só se o modelo
      tem `Vision` (3a; seleção sem visão bloqueia anexo verificado).
- [x] Contexto: `@arquivo`, `@solução`, seleção ativa do editor, output window
      (3c, 2026-11-08; snapshots explícitos, confinamento e envio verificados na Exp).
- [x] Histórico de conversas persistente, busca, renomear, excluir (3b,
      2026-11-08; paginação e virtualização medidas verificadas na Exp).
- [x] Cancelamento visível e responsivo durante stream e durante tool
      (2026-11-12: stream 623 ms, ferramenta 95 ms com processo morto).
- [x] Estados de erro acionáveis (chave inválida → abrir settings;
      429 → mostrar tempo de espera) (3d, 2026-11-09; foco no campo certo e
      espera de 90 s vinda do `Retry-After` verificados na Exp).
- [x] Acessibilidade: navegação por teclado, ARIA, screen reader, contraste
      (3d, 2026-11-09; skip link, `Ctrl+Enter`, live region e contraste verificados;
      passada manual de leitor de tela pendente do usuário).
- [x] Localização PT-BR e EN (resources no C# + dicionário no JS).
- [x] CSP restritiva no `index.html`; assets 100% locais (reload offline com
      12 assets de `https://lumen.local` verificado).

### Andamento — aceitação consolidada (2026-11-12)

Critérios integrais verificados na Exp com o build atual: colar 1 MiB em 97 ms
com clamp/negativo, markdown malicioso inerte, mensagens inválidas rejeitadas,
cancelamento de stream/ferramenta < 1 s sem estado inconsistente, sequências
contíguas. Detalhes e causa raiz dos stalls anteriores (build de worktree
paralelo implantado na Exp) em `STATUS.md`. Fase 3 concluída.

### AC

- Conversa com streaming fluido, sem flicker e **sem travar a UI do VS**.
- Cancelar durante o stream para em < 1 s e não deixa tool órfã.
- Anexar imagem num modelo sem visão é **impossível** pela UI.
- Navegação completa por teclado.
- Exercitado na experimental instance com evidência (screenshot ou log).
- A UI carrega **sem nenhuma acesso à rede**.

### CN

- Colar 1 MB de texto no composer não pode congelar a UI nem o VS.
- Resposta com markdown malicioso (`<script>`, `javascript:`, `<img onerror>`,
  imagem externa rastreável) deve ser **sanitizada** — teste asserta que nenhum
  script executa, nenhum handler inline sobrevive e nenhum link perigoso fica
  clicável.
- Mensagem JS com schema inválido / id de correlação desconhecido / payload
  gigante → **rejeitada**, sem crash.
- Cancelar no meio de um tool call não pode deixar arquivo parcialmente escrito
  (integra com a Fase 4).
- Streaming com delta fora de ordem ou duplicado → UI não corrompe.

### Commit

`feat: add WebView2 chat UI with streaming, reasoning blocks and tool cards`

---

## Fase 4 — Ferramentas e camada de segurança

**Objetivo:** a IA lê, edita e cria arquivos e executa comandos — com a política
de aprovação do usuário implemented exatamente.

### Andamento — fatia 4a (2026-11-09)

Base de backend implementada: `Lumen.Tools` net48, `WorkspaceGuard`,
`ApprovalBroker` e `read_file` / `list_dir` / `find_files` / `grep` com schemas
estritos. Build Release sem erros/avisos; 1.244 testes C# e 160 JS verdes.
Controles negativos exercitam junction real, handle aberto fora da raiz,
traversal, argumentos inválidos e negação de escrita/comandos em Ask/Plan.
Criação de symlink de arquivo indisponível neste Windows: cobertura não alegada.

Registro histórico da 4a: ainda sem host/UI ou Exp naquele checkpoint; a 4b
abaixo substitui esse estado e o TFM inicial. Prompts eram interface com fake.

### Andamento — fatia 4b (2026-11-09)

As quatro ferramentas readonly estão integradas por RPC e formulário explícito,
sem provedor/modelo nem tokens; resultados plaintext não entram no chat/contexto.
O host fixa modo Ask e raiz da solução, executa em background e revalida a solução.
`Lumen.Tools` alinhado a netstandard2.0 após falha real de identidade JSON na Exp;
o verificador do pacote rejeitou o pacote incompatível antes da correção.
Build Release 0 erros/avisos; **1.261 testes C# / 166 JS** verdes. Na Exp: leitura
do disco (não buffer), busca/listagem/range, truncamento, HTML inerte, recuperação,
203 px sem overflow e solução fechada rejeitada; controles de path, binário,
tamanho e autoridade forjada exercitados. Evidências e lacunas em `STATUS.md`.

Registro histórico da 4b: edição, diff/undo e buffer ainda pendentes naquele
checkpoint. Invocação automática readonly foi antecipada com autorização na
Fase 5; as fatias seguintes não concluem os critérios integrais automaticamente.

### Andamento — fatia 4c: escrita manual interna (2026-11-10)

Criação/edição explícitas em painel separado, com diff plaintext e undo do lote
manual em memória. Arquivo aberto usa buffer MEF sem salvar/recarregar e histórico
nativo do VS; fechado usa gravação atômica + journal limitado. Captura da solução,
path relativo e schema estrito precedem a execução, sem settings/credenciais/API.
Mudanças posteriores no disco/buffer, versões e marcadores de undo divergentes
são recusados; não depende de `UndoStack`, não suportado pelo VS instalado.
Diff mostra finais de linha e truncamento. Cancelamento tardio não oculta commit.
Clean/build 0 erros/avisos; **1.405 C# / 197 JS** verdes. Verificador manual
completo passou na Exp, inclusive duas edições do mesmo buffer e controles
negativos, com 0 pagos/0 exceções JS; cleanup e SHA finais confirmados.
Nesse checkpoint a regressão readonly automática ficou parcial por captura CDP
(B9); uma execução posterior terminou integralmente, conforme `BLOQUEIOS.md`.
A conclusão desta fatia não conclui as Fases 4/5.

Registro histórico: a 4c não habilitava escrita pelo modelo, undo de turno
Agent, aprovação externa ou terminal. Esses itens foram tratados na 4d abaixo.

### Andamento — fatia 4d: escrita pelo modelo e terminal aprovado (2026-11-11)

Opt-in por envio, schema/autoridade restritos, escrita interna automática com
resultado/diff e undo isolado do último turno. Manuais consecutivas mantêm seu
próprio lote. Escrita externa exige aprovação por operação, sem consentimento
por sessão. Terminal oferece negar/permitir uma vez/permitir nesta sessão;
fechar/reabrir solução revoga consentimento mesmo com o mesmo path.

Processo Windows suspenso é associado ao Job Object antes de executar;
encerramento de descendentes e drenagem limitada cobrem timeout/cancelamento.
Output público recebe comando, stdout/stderr e exit code. Orçamento é reservado
antes de escrita; cancelamento tardio preserva diff de commit já realizado.

Clean/build **0 erros/avisos**, **1.466 C# / 205 JS** verdes. Exp passou
manual+dois turnos automáticos e undo isolado; terminal negado/permitido uma vez,
Output, cancel, consentimento de sessão/revogação, readonly, negação externa,
HTML/segredos, solução fechada e layout negativo. Verificação dividida em execução
inicial e retomada após corrigir inspeção COM do robô, sem repetir as escritas.
Retomada: **11 requests loopback, 0 pagos, 0 exceções JS**, screenshot; cleanup e
SHA finais confirmados. Evidências e limites em `STATUS.md`/`COMANDOS.md`.

### Andamento — diagnósticos e shells (2026-11-11)

`apply_diff`/`get_edit_snapshot`, `get_errors`, `get_output_logs`, descoberta de
shells e `run_build`/`run_tests` .NET foram implementados. Execução de build e
testes usa `--no-restore`, alvo confinado e aprovação do terminal. Resultado:
**1.493 C# / 205 JS**, build sem erros/avisos; Exp com 33 requests loopback,
0 pagos e 0 exceções JS validou patch/undo, hash incorreto, diagnósticos, shell,
negação de build/test e build autorizado. Detalhes e limites em `STATUS.md`.

A Error List pode ser filtrada/desatualizada; o Output é apenas a pane ativa.
A Exp não testou Error List populada nem teste .NET aprovado pela UI. `run_tests`
não é integração universal com Test Explorer. B9 foi encerrado após nova
regressão readonly completa; modos integrais pertencem à Fase 5.

### Tarefas

- [x] **Workspace guard**: canonicalização + confinamento por prefixo,
      rejeição de traversal, symlink para fora, UNC não autorizado
      (4a; junction real e handle de identidade errada exercitados; symlink de
      arquivo exige elevação/Developer Mode — limitação registrada).
- [x] File tools: `read_file` (com range de linhas), `find_files` (glob),
      `list_dir`, `grep`, `create_file`, `edit_file`, `apply_diff`
      (4b–4d + snapshot/patch; verificados na Exp).
- [x] `edit_file` com **diff visível** e registro de undo; **undo em lote**
      reverte o turno inteiro.
- [x] Edição em arquivo **aberto no editor** deve sincronizar com o buffer:
      MEF `ITextBuffer.CreateEdit()` / `ITextEdit.Apply()`, com acesso à UI thread
      via `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync`.
      Undo real do VS, não só arquivo em disco (`ARQUITETURA.md` §2.5).
- [x] Shell tools: `run_command` com detecção de cmd / powershell / pwsh /
      bash (Git Bash), captura de stdout+stderr+exit code, timeout,
      cancelamento, e leitura de output em background.
      **Não existe API pública de terminal no VS** — usar
      processo próprio + pane no Output Window (`IVsOutputWindowPane`).
      A 4d usa `CreateProcessW` suspenso + Job Object para confinar descendentes.
      Não tentar usar
      `Microsoft.VisualStudio.Terminal.dll` (serviço interno, não suportado).
- [x] **Nunca** concatenar argumentos não escapados para montar comando.
      Lista de argumentos, escaping Windows validado por `CommandLineToArgvW`
      e caminho do executável resolvido explicitamente.
      Todo o trabalho de processo roda **fora da UI thread**.
- [x] `ApprovalBroker` implementando `AGENTS.md` §1:
      leitura livre · edit dentro da solução automático · fora do workspace
      confirma · **todo** comando confirma com "sempre permitir nesta sessão"
      (negação/allow-once/sessão e revogação verificados na Exp).
- [x] Blocklist de destrutivos como defesa em profundidade (12 padrões;
      bloqueia antes do prompt, nunca substitui a confirmação).
- [x] Tools de diagnóstico: `get_errors` (Error List), `get_output_logs`,
      `run_build`, `run_tests` se acessível (Error List populada e teste .NET
      real aprovado/reprovado pela UI em 2026-11-12).
- [x] Toda tool com JSON schema validado **antes** da execução.

### Andamento — aceitação consolidada (2026-11-12)

AC e CN exercitados na Exp com o build atual: ler/criar/editar com diff e
comando aprovado; undo em lote; buffer visível atualizado; escrita externa
negada → permitida → undo; `dotnet test` real aprovado e reprovado; Error List
populada com negativo; cancelamento de stream (623 ms) e de ferramenta (95 ms)
com processo morto e sem sobrevivente; Ask/Plan rejeitam escrita/terminal no
broker; argumentos literais sem shell injection; schema antes da execução.
**12 + 37 + 6 requests loopback, 0 pagos, 0 exceções JS**; 1.505 C# / 207 JS
verdes. Limitações: symlink de arquivo sem elevação e leitor de tela manual
pendentes do usuário. Detalhes em `STATUS.md`. Fase 4 concluída.

### AC

- A IA consegue: ler um arquivo, criar outro, editar um terceiro com diff,
  e rodar um comando — tudo com a política correta.
- Undo em lote reverte todas as edições de um turno.
- Editar arquivo aberto no editor atualiza o buffer visível.
- Testes verdes, incluindo os de segurança.

### CN — **os mais importantes do projeto**

- `read_file("../../../../Windows/win.ini")` → **rejeitado**.
- Path com symlink apontando para fora do workspace → **rejeitado**.
- Escrita fora do workspace → **exige confirmação** (teste asserta o prompt).
- `run_command` com `rm -rf /`, `del /s /q`, `format`, `git push --force`,
  `git reset --hard`, `shutdown`, `curl … | sh` → **bloqueado ou exige
  confirmação explícita**, nunca executa silenciosamente.
- Argumento com `; rm -rf`, `&&`, `|`, backtick, `$( )` → tratado como
  **argumento literal**, não como shell injection.
- JSON de argumentos inválido → tool **não executa**.
- Modo **Plan** e modo **Ask**: tentar chamar `create_file` / `edit_file` /
  `run_command` → **rejeitado pelo broker**, não só escondido da UI.
- Timeout de comando → processo realmente morto, sem zumbi.

### Commit

`feat: add file and shell tools with workspace guard and approval broker`

---

## Fase 5 — Loop do agente e modos

**Objetivo:** Agent / Ask / Plan funcionando de ponta a ponta.

### Fatia readonly antecipada — autorização em 2026-11-09

O usuário solicitou explicitamente invocação automática de ferramentas pelo
modelo após a 4b. Escopo desta antecipação: somente as quatro ferramentas de
leitura existentes, retorno dos resultados ao modelo, limites/cancelamento e
histórico íntegro. Não habilita escrita, terminal, modos completos ou conclusão
da Fase 4/5. Continuação automática exige opt-in por envio, com aviso de conteúdo
de disco enviado ao provedor e custo potencial; testes usam apenas mocks/loopback.

Implementação: `ReadToolChatLoop` monta rodadas assistant/tool, preserva estado
assinado e expande o histórico sem misturar chamada e resultado. Captura da
solução precede credenciais/HTTP; executor continua Ask, somente disco. Opt-in
é desmarcado após cada envio; painel manual não alimenta o modelo. Tetos fixos:
quatro requests, oito ferramentas, 8.192 tokens de saída solicitados e dois
minutos por turno, sem retry ou compactação silenciosa. Budget interrompe o
ciclo; cancelamento não desbloqueia antes da resposta nativa. Resultados e
argumentos ficam inertes na UI; rejeições não executam ferramentas desconhecidas.

Verificação: clean/build Release **0 erros/avisos**, **1.310 C# / 179 JS** verdes.
Na Exp com provedor loopback: leitura/reinjeção, disco versus buffer, histórico,
HTML inerte, opt-out, traversal/escrita negados, limite de loop, cancelamento e
recuperação; solução fechada sem POST, 203 px sem overflow, **0 exceções JS**.
Foram 12 requests locais, nenhuma API paga. Evidências/lacunas em `STATUS.md`.
Não marcar tarefas integrais abaixo: modos, escrita, configuração do orçamento,
compactação e retry continuam pendentes.

### Andamento — seleção de modos (2026-11-11)

Agent/Ask/Plan selecionáveis no chat; Ask inicial e consentimentos revogados ao
mudar. O host recusa escrita/terminal em Ask e Plan mesmo com flags forjadas,
sem captura ou POST. Prompts por modo são enviados também sem ferramentas e
histórico é preservado na troca. O painel manual continua independente do chat.
**1.505 C# / 206 JS** verdes; Exp com loopback validou Agent/undo e rejeitou
escrita/terminal forjados em Ask/Plan, 6 requests locais e 0 pagos. Execuções
anteriores do verificador geral foram interrompidas por falhas do robô/fixture;
detalhes em `STATUS.md`. Ainda sem botão aplicar plano ou critérios integrais.

Reverificação em 2026-11-12 com o build atual (após remover o pacote de um
worktree paralelo que estava implantado na Exp): modos **6 requests, 0 pagos**,
regressão geral **37 requests** e aceitação consolidada **12 requests**, todos
loopback; **1.505 C# / 207 JS** verdes. Fases 3/4 fechadas; Fase 5 segue aberta
para aplicar plano, orçamento, compactação e retry.

### Andamento — fatia 5b: aplicar plano + orçamento (2026-11-12)

`ToolBudget` no domínio (rodadas 1–8, tokens acumulados 1024–32768, invocações
1–32; padrão 4/8192/8) parameteriza o `ToolChatLoop`; o host valida o campo
opcional `budget` antes de capturar workspace, ler credenciais ou chamar o
provedor. A UI tem painel de orçamento por envio (bloqueia envio fora da faixa)
e botão "aplicar plano" visível somente em Plan: muda para Agent, revoga
consentimentos, anuncia e foca o compositor, sem nenhum POST e preservando a
conversa. Build Release **0 erros/avisos**; **1.527 C# / 210 JS** verdes.
Exp com loopback (`plan-budget`): plano → aplicar → rodadas=1 interrompe após
1 request sem executar tool → budgets forjados rejeitados pelo host → envio
bloqueado com valor inválido → orçamento maior conclui na mesma conversa;
**4 requests locais, 0 pagos, 0 exceções JS**, layout 203 px com negativo.
Evidência: `artifacts/phase5-plan-budget-webview.json`; instalação SHA em
`Extensions\vhheq1gz.wvu`. Ainda faltam ContextBuilder integral, compactação,
ParallelTools e retry (fatia 5c), além do AC de tarefa real multi-passos.

### Andamento — fatia 5c: compactação + retry + paralelismo (2026-11-13)

`ToolChatLoop.Compact` descarta turnos inteiros mais antigos (fronteira =
mensagem User), preservando o prefixo system e o turno mais novo; estimativa
de tokens por tamanho (1 char ≈ 1 token) e limite
`min(262144, ContextWindowTokens − outputTokens − 2048)`. `ValidateInput`
rejeita input atual oversized com `context_too_large` **antes** de capturar
workspace, ler credenciais ou POST; compactação roda pré-chat e no início de
cada rodada e emite Notice `history_compacted` (uma vez por turno). Retry:
até 2 retentativas por rodada (3 tentativas), somente `rate_limited` /
`provider_error` sem saída visível já emitida; respeita `Retry-After`, senão
backoff exponencial com jitter; Notice `retrying`; ids de tool da tentativa
falha são descartados e tools já executadas **não** reexecutam. Paralelismo
só com `calls.Length > 1 && ParallelTools && todas ParallelSafe` — escritas,
terminal, build e tests nunca paralelizam; OpenRouter deriva `ParallelTools`
de `supported_parameters`. UI: Notices localizados (pt-BR/EN) como linha
secundária + anúncio aria-live; bridge só aceita os dois códigos. Build
Release **0 erros/avisos**; **1.549 C# / 212 JS** verdes, com controles
negativos: 401 após tool não retenta, input atual oversized rejeitado
pré-credencial, compactação nunca órfã par tool_call/tool_result (assert de
invariante), escritas nunca paralelas. Exp com loopback (`loop-resilience`):
20 turnos Ask em modelo de janela 8192 compactam com Notice e replay
encolhido; 429 (Retry-After 1) após tool retenta com 3 requests e **exatamente
1 execução** de tool, id do resultado pareado com a chamada; modelo com
`parallel_tool_calls` executa 2 leituras em paralelo e modelo sem a
capacidade executa em sequência; **27 requests locais, 0 pagos, 0 exceções
JS**. Evidência: `artifacts/phase5-loop-resilience-webview.json`; instalação
SHA em `Extensions\f3dzgvrn.jkl`. Sobreposição temporal do paralelismo é
provada pelos testes C# (MaxActive), não pelo verificador. Ainda faltam
ContextBuilder integral e o AC de tarefa real multi-passos (fatia 5d).

### Andamento — fatia 5d: ContextBuilder + tarefa real multi-passos (2026-11-13)

**5d-1 — ContextBuilder integral.** O compositor parseia `@arquivo` e
`@solução`/`@solution` (`parseMentions`: deduplica por caminho normalizado,
nunca casa e-mail inline). Antes de criar o turno, cada menção é resolvida via
`byok.context.capture` — buffer aberto preferido sobre disco, path
canonicalizado e confinado ao workspace. Menção já anexada pelo painel não
captura duas vezes; os limites de contexto (8 itens / tamanho total) bloqueiam
o envio localmente. Falha de captura bloqueia o envio, preserva o rascunho e
nomeia o token com motivo localizado (`byokMentionFailed`); traversal `../`
é rejeitado pelo guard (controle negativo na Exp). O contexto capturado entra
na mesma mensagem User como segundo TextPart `[Context: label]`. System prompt
por modo, seleção do editor e estrutura da solução já existiam (fases 3c/5a);
**skills ativas ficam para a Fase 6** (dependem do sistema de Skills, ainda não
implementado). Teste C# novo prova create→edit→build realimentando cada
resultado antes da próxima rodada, com aprovação de terminal e cards
Completed no histórico. Build Release **0 erros/avisos**; **1.550 C# / 217 JS**
verdes, com 5 testes JS novos de menção (positivo, bloqueado sem POST,
duplicata, `@solution`, limite de contexto).

**5d-2 — AC de tarefa real multi-passos.** Estágio `agent-task` do verificador
na Exp real com provedor loopback roteirizado (sem API paga): menção positiva
injeta contexto do buffer aberto; menção traversal é bloqueada sem POST; o
modelo cria `Calculator.cs`, `CalculatorTests.cs` e um `Calculator.csproj`
próprio e executa `run_build` aprovado — exit 0 prova que **ambos** os fontes
compilaram juntos (o teste referencia `Calculator.Add(2, 3)`, então CS0103
derrubaria o build se qualquer fonte faltasse); undo em lote restaura os 3
arquivos; colisão mid-task (`file_exists`) recupera sem duplicar escrita.
**8 requests loopback, 0 pagos, 0 exceções JS**. O orçamento precisa ser
8/32768/16: são 4 rodadas de tool + resposta = 5 rodadas, e o padrão 4 aborta
a rodada do build com `tool_budget_exceeded` (o orçamento configurável
funcionando como projetado). Evidência:
`artifacts/phase5-agent-task-webview.json`; instalação SHA em
`Extensions\4hgcddl3.sps`.

**Achado documentado (não é bug do produto):** o `.csproj` legado da fixture é
mantido pelo project system do VS em um buffer RDT somente-leitura cujo
snapshot muda continuamente; `edit_file` corretamente recusa com
`edit_conflict` — inclusive em solução recém-carregada. O verificador contorna
criando um projeto dedicado em vez de editar o csproj aberto. Curingas
`<Compile Include="*.cs" />` ou SDK-style foram rejeitados como alternativa:
puxariam o `AcceptanceTests.cs` do estágio de aceitação e quebrariam o
`BuildFixtureError` (que builda a solução inteira).

### Tarefas

- [x] `AgentLoop`: monta contexto → chama provider → executa tools → realimenta
      → repete até concluir (`ToolChatLoop`; rodadas, tokens e invocações
      limitados; verificado na Exp com loopback).
- [x] Orçamento: máx de iterações, máx de tokens, máx de tools por turno.
      Configurável. Ao estourar, para e explica — não continua silenciosamente.
      (`ToolBudget` 1–8 rodadas / 1024–32768 tokens / 1–32 invocações; painel
      por envio; host rejeita fora da faixa antes de captura/POST; verificado
      na Exp com loopback em 2026-11-12.)
- [x] `ContextBuilder`: system prompt por modo, seleção do editor, @arquivos,
      skills ativas, estrutura da solução. (@arquivos + contexto unificado
      verificados na Exp em 2026-11-13; system prompt/seleção/estrutura já
      existiam desde 3c/5a. **Skills ativas → Fase 6**, dependem do sistema de
      Skills ainda não implementado.)
- [x] **Context window management**: contagem de token, truncamento/compactação
      de histórico antigo preservando tool calls pendentes. (`Compact` por
      turnos inteiros + `ValidateInput` pré-credencial; Notice
      `history_compacted`; invariante de pares assertada em teste e na Exp;
      verificado com loopback em 2026-11-13.)
- [x] Paralelismo de tools só se o modelo tem `ParallelTools`. (Gate triplo:
      múltiplas chamadas + capacidade + todas ParallelSafe; escritas/terminal
      nunca paralelizam; sobreposição provada por teste C# MaxActive e
      sequência/paralelo na Exp em 2026-11-13.)
- [x] Modo **Plan**: produz plano em fases com critérios de aceite;
      oferece "aplicar plano" que muda para Agent. (Prompt de plano verificado;
      botão só aparece em Plan, troca para Agent sem POST e preserva histórico;
      a qualidade do plano depende do modelo.)
- [x] Modo **Ask**: read-only, respostas com citações `arquivo:linha`
      (política e prompt verificados; qualidade da citação depende do modelo).
- [x] Troca de modo no meio da conversa preservando histórico (verificado na
      Exp: mesma conversa após Agent → Plan → Ask).
- [x] Retry com backoff em falha transitória, sem duplicar tool já executada.
      (Até 2 retentativas só para `rate_limited`/`provider_error` sem saída
      visível; `Retry-After` ou exponencial+jitter; Notice `retrying`; teste
      e Exp provam 1 execução de tool após 429; 401 não retenta.)

### AC

- Tarefa real de múltiplos passos (ex.: "crie uma classe X com teste e faça
      build") conclui no modo Agent com verificação. (**Verificado** no estágio
      `agent-task` da Exp com loopback em 2026-11-13: create→create→create
      project→build aprovado, exit 0 com os dois fontes compilados, undo em
      lote e colisão mid-task recuperada; 8 requests locais, 0 pagos.)
- Modo Plan produz plano e **não escreve nada**.
- Modo Ask responde e **não escreve nada**.
- Estourar o orçamento para com mensagem clara.
- Cancelar no meio do loop não deixa estado inconsistente.

### CN

- Modelo que entra em **loop de tool calls** (chama a mesma tool idêntica
  repetidamente) deve ser interrompido pelo orçamento, não rodar para sempre.
- Modelo que pede uma tool **inexistente** → erro controlado devolvido ao
  modelo, não exceção.
- Compactação de histórico **não pode** órfã um `tool_call` sem seu
  `tool_result` — isso quebra a próxima chamada na maioria dos provedores.
  Teste asserta a invariante.
- Falha transitória após uma tool já executada → retry **não reexecuta** a tool.

### Commit

`feat: add agent loop with Agent/Ask/Plan modes and context budgeting`

---

## Fase 6 — Skills

**Objetivo:** sistema de Skills extensível pelo usuário.

### Tarefas

- [x] Formato: pasta com `SKILL.md` + frontmatter YAML (`name`, `description`),
      arquivos de apoio opcionais. **Adotar o padrão agentskills.io**, que é o
      mesmo do Agent Skills nativo do VS 2026 (desde 18.5) — assim as skills que
      o usuário já tem funcionam sem conversão (`ARQUITETURA.md` §2.10). *(6a)*
- [x] Discovery em `.lumen/skills/`, `.github/skills/`, `.claude/skills/`,
      `.agents/skills/` (projeto) e `~/.lumen/skills/`, `~/.copilot/skills/`
      (usuário). Precedência: projeto > usuário; no projeto,
      `.lumen/` > `.github/` > `.claude/` > `.agents/`. *(6a)*
- [x] **Progressive disclosure**: só `name` + `description` no system prompt;
      corpo carregado sob demanda quando acionada. *(6a)*
- [x] Acionamento: automático por descrição *(6d)*, explícito por `/nome` *(6a)*, e manual
      pela UI *(6a/6b)*.
- [x] Skill pode declarar ferramentas próprias e restringir modos. *(6a; `allowed-tools`
      informativo, `lumen-modes` restritivo — nunca concedem permissões)*
- [x] UI: listar, ativar/desativar, criar, editar, abrir a pasta, recarregar. *(6b/6c)*
- [x] Validação de frontmatter com erro acionável. *(6a)*
- [x] Algumas skills de exemplo embarcadas (ex.: revisão de código, escrever
      testes, explicar seleção). *(6c)*

### AC

- Criar uma skill em `.lumen/skills/` e vê-la aparecer na UI sem reiniciar o VS.
- Skill acionada injeta o corpo no contexto; skill não acionada **não** injeta.
- Desativar uma skill remove seu efeito imediatamente.

### CN

- `SKILL.md` sem frontmatter ou com `name` duplicado → **erro de validação
  claro**, não falha silenciosa nem skill duplicada.
- Skill com `description` gigante não pode estourar o system prompt —
  teste asserta o limite.
- Skill que declara modo proibido não pode habilitar escrita em modo Plan.
- Path de skill fora dos diretórios permitidos → **rejeitado**.

### Commit

`feat: add user-extensible Skills system with progressive disclosure`

---

## Fase 7 — Personalização visual

**Objetivo:** temas e ajustes visuais, incluindo seguir o tema do VS.

### Tarefas

- [x] Seguir o tema do VS (dark / light / high contrast) por padrão, reagindo ao
      evento `VSColorTheme.ThemeChanged` e reinjetando as CSS variables no
      WebView2 — **sem reload** da página. *(7a)*
- [x] Temas próprios em JSON, materializados como **CSS variables**: cores,
      fonte do chat, fonte de código, tamanho, densidade, raio de borda,
      cor de destaque por modo. *(7a — modelo, validação e materialização;
      painel de edição em 7b)*
- [x] Preview ao vivo antes de aplicar; reset para o padrão. *(7a/7b)*
- [x] Importar/exportar tema. *(7c — importação em prévia; exportação JSON)*
- [x] Persistência por usuário, não por solução. *(7a — `%LocalAppData%\Lumen\providers.theme.json`)*
- [x] Verificar contraste WCAG AA nos temas embarcados. *(7c — texto 4,5:1,
      destaques 3:1 e tokens de link, foco, sintaxe e botão)*

### AC

- Trocar o tema do VS muda o chat na hora, sem reiniciar. *(7a)*
- Tema custom aplicado e persistido entre sessões. *(7a)*
- Reset volta ao padrão. *(7a/7b)*

### CN

- Tema JSON inválido → **rejeitado com erro**, não deixa a UI ilegível. *(7a)*
- Tema com contraste insuficiente → **aviso** na UI. *(7b)*
- Combinação fonte/tamanho extrema não pode quebrar o layout. *(verificação 7b em 320×240)*

Fechamento consolidado em 2026-11-16, incluindo troca real de tema do VS sem
reload na hive experimental; evidências em `docs/STATUS.md`.

### Commit

`feat: add theming with VS theme sync and custom visual presets`

---

## Fase 8 — Hardening, empacotamento e entrega

**Objetivo:** extensão instalável e confiável por outra pessoa.

### Tarefas

- [x] Revisão de segurança: paths, shell injection, sanitização de markdown,
      vazamento de chave, permissões do broker. *(8a/8b; lacuna de validação de
      esquema de URL na host corrigida em 8b)*
- [x] Teste de carga: conversa longa (200+ mensagens), muitos arquivos,
      output de comando grande. Sem leak de memória. *(8d — 220 turnos, limites
      de leitura e output truncado; teste de UI/histórico de 500 turnos já existente)*
- [x] Teste em VS 18 **estável** se disponível, além do Insiders. *(8d —
      `vswhere -prerelease -all` encontrou apenas o Insiders; estável não está
      instalado nesta máquina)*
- [ ] Ícone e preview image de marketplace. *(B12: decidido adiar em 2026-11-17)*
- [x] Display name, descrição e tags de marketplace no manifesto. *(8c)*
- [x] `LICENSE` (MIT), `README` de usuário final, `CHANGELOG`. *(8c)*
- [x] Guia de instalação e de primeiro uso no `README.md`. *(8c)*
- [x] VSIX de Release determinístico; verificar que nenhuma chave, `bin/`,
      `obj/` ou segredo entrou no pacote. *(8c: dois builds limpos com o mesmo
      SHA-256 após normalização de timestamps e do `extensionDir`)*
- [x] Diagnóstico: log estruturado **sem segredos**, nível configurável,
      botão "abrir log". *(8c)*
- [x] Telemetria: **nenhuma** por padrão. *(8c: ausência confirmada por varredura
      do pacote; nenhum SDK de telemetria referenciado)*

### AC

- O `.vsix` instala em uma máquina limpa com VS 18 e funciona sem configuração
  além da chave.
- Nenhuma chamada de rede acontece antes de o usuário configurar um provedor. *(verificado em 8a; verCN abaixo)*
- Inspeção do pacote: zero segredos, zero `bin/obj`.
- Todos os testes verdes, todos os CN das fases anteriores ainda passando.

### CN

- Instalar e abrir **sem nenhuma chave configurada**: a extensão deve abrir,
  mostrar o onboarding e **não fazer nenhuma requisição de rede**. *(sessão sem
  chave verificada em 8a por teste C# com transporte que falha se chamado; na
  Exp foi medido o startup sem requisições externas, com a configuração atual
  do usuário preservada)*
- Procurar a chave de teste em todo o conteúdo do `.vsix` e nos logs gerados →
  **zero** ocorrências.
- Rodar a suíte inteira de CN de segurança uma última vez → tudo ainda rejeita.

### Commit

`chore: harden, document and package Lumen for distribution`

---

## Fase 9 — Subagentes e delegação

> **Fechamento em 2026-11-17:** as fatias 9a–9c entregaram delegação local
> determinística, descoberta por pasta, limites de modo/profundidade/concorrência
> e painel na UI. Itens do plano que exigem chamadas de modelo por subagente,
> perfis/modelo/orçamento, histórico próprio com fontes e edições concorrentes
> **não foram implementados** e estão listados como limitações em `STATUS.md`.
> A fase é marcada como concluída com essas limitações registradas, conforme
> solicitação do usuário; não se alega cobertura integral do escopo original.

**Objetivo:** permitir que o agente principal delegue tarefas independentes a
subagentes especializados e reúna resultados verificáveis, sem enfraquecer as
políticas de modo, aprovação, privacidade e orçamento do Lumen. Executar depois
das fases 0–8; não é requisito para concluir a entrega da Fase 8.

### Tarefas

- [ ] Definir o contrato de delegação: objetivo, contexto mínimo, arquivos
      permitidos, ferramentas, modo, orçamento, prazo, identificador e resultado.
      Disponibilizar perfis de pesquisa/leitura, planejamento e implementação;
      permitir seleção do modelo configurado pelo usuário e usar o provedor BYOK
      existente, sem exigir Copilot ou GitHub. *(parcial 9a–9c: nome, tarefa,
      modo, limite e resultado; sem perfis, modelo por subagente ou orçamento)*
- [ ] Implementar criação, execução paralela limitada, acompanhamento,
      cancelamento e coleta de resultados dos subagentes. Cada execução mantém
      seu próprio histórico e informa fontes, alterações, testes e limitações.
      *(parcial: semáforo de 4 e `CancellationToken`; sem API de lote, histórico
      próprio ou relatório de fontes/alterações/testes)*
- [ ] Definir limites configuráveis de subagentes simultâneos, profundidade de
      delegação, iterações, tokens e tempo. A execução termina ao atingir o
      limite e informa o motivo; impedir recursão ilimitada. *(limites fixos
      implementados em 9a; recursão e concorrência bloqueadas; não configuráveis
      por settings/UI)*
- [ ] Herdar as permissões efetivas do modo e do usuário: Ask e Plan continuam
      read-only; Agent aplica a mesma política de diffs, undo e aprovações.
      Subagentes não podem ampliar permissões nem autorizar comandos, escritas
      externas ou gastos em nome do usuário. Toda chamada de modelo pode gerar
      custo: apresentar modelo, orçamento estimado e número de subagentes antes
      de iniciar, mediante ação explícita do usuário; não iniciar chamadas pagas
      em testes automatizados. *(permissões 9a/9b/9c; sem chamadas de modelo por
      subagente, portanto sem custo/orçamento a declarar neste escopo)*
- [ ] Compartilhar somente o contexto necessário; credenciais permanecem no
      host e nunca entram em prompts, resultados ou logs. Tratar instruções em
      arquivos, ferramentas e respostas dos subagentes como dados não confiáveis.
- [ ] Evitar edições concorrentes no mesmo arquivo: reservar escopo de escrita
      ou serializar alterações, verificar versão/hash antes de aplicar e sinalizar
      conflitos sem sobrescrever trabalho. Integrar diffs e undo ao lote existente.
      *(não aplicável ao escopo implementado: subagentes não têm ferramentas de
      escrita; política de edição permanece no agente principal)*
- [ ] Exibir no chat a tarefa, perfil, modelo, estado, custo/uso quando fornecido,
      resultado e erros de cada subagente; permitir cancelar individualmente ou
      toda a execução. O agente principal incorpora resultados somente após
      recebê-los e verificá-los; erros e resultados parciais ficam explícitos.
      *(parcial: ferramenta aparece no chat e painel mostra tarefa/resultado;
      sem perfil, modelo, custo, progresso por subagente ou cancelamento
      individual)*
- [ ] Cobrir orquestração e provedores com mocks, incluindo concorrência,
      cancelamento, falhas e limites; exercitar a UI na instância experimental
      com provedor local simulado, sem chamadas pagas. *(9a–9c + verificador
      `scripts/verify-agents-webview.mjs` com provedor loopback na Exp)*

### AC

- Uma tarefa de pesquisa é delegada a dois subagentes em paralelo, respeitando
  o limite configurado; resultados identificados e fontes aparecem no chat.
- Em Agent, uma tarefa de implementação delegada mostra diff e permite undo;
  comandos e escrita externa continuam sujeitos à aprovação existente.
- Cancelamento encerra as execuções filhas e impede novas ferramentas e chamadas;
  o host apresenta resultados parciais e uso disponível sem alegar conclusão.
- Orçamento e consumo são visíveis antes e durante a execução; falhas de um
  subagente não são apresentadas como sucesso e não corrompem a conversa.
- Build e testes verdes, controles negativos executados e UI exercitada na Exp;
  se a validação na Exp estiver bloqueada, registrar a limitação e não marcar a
  fase como concluída.

**Status dos ACs no fechamento:** o último AC foi verificado na Exp (loopback,
0 chamadas pagas). Os ACs que exigem dois subagentes paralelos com fontes no
chat, delegação de implementação com diff/undo, cancelamento de execuções filhas
e orçamento/consumo visíveis **não foram atendidos** pelo escopo implementado e
constam como limitações em `STATUS.md`.

### CN

- Subagente em Ask/Plan que solicita escrita ou comando é rejeitado pelo host,
  inclusive quando forja o modo ou suas permissões.
- Subagente não consegue iniciar comando ou escrita externa sem a aprovação
  exigida, nem herdar uma aprovação expirada ou de outra solução.
- Duas tarefas tentam editar o mesmo arquivo ou aplicar diff de versão antiga:
  uma aguarda ou recebe conflito; nenhuma alteração existente é sobrescrita.
- Estouro de orçamento, profundidade ou concorrência bloqueia novas execuções;
  falhas e cancelamento não deixam processos ou chamadas filhos ativos.
- Resultado de subagente que contém instruções maliciosas ou credencial de
  teste não altera permissões e não aparece sem redação em logs ou UI.
  *(verificado: resultado é texto inerte; `ToolChatLoop.RejectSecret` barra a
  chave da sessão antes do provider — teste de controle negativo em
  `AgentToolTests`)*

### Commit

`feat: add bounded subagent delegation with inherited permissions`

---

## Definition of Done global

A extensão está pronta quando **tudo** abaixo é verdade:

1. Instala no VS Community 2026 (18.x) e abre a tool window Lumen.
2. Colar uma BYOK lista os modelos automaticamente com badges de **visão**,
   **raciocínio** e **tools**, mostrando a origem de cada capacidade.
3. Os três modos funcionam e respeitam a matriz de permissões.
4. A IA lê, cria e edita arquivos com diff + undo em lote, e executa comandos
   em cmd / PowerShell / bash — com a política de aprovação exata.
5. Skills do usuário são descobertas e acionadas com progressive disclosure.
6. Temas funcionam e seguem o tema do VS.
7. Chaves no Credential Manager; zero segredo em log, settings ou pacote.
8. Suíte de testes verde **com todos os controles negativos**.
9. Nenhuma chamada paga sem ação explícita do usuário.
10. `docs/STATUS.md` reflete a realidade e `docs/BLOQUEIOS.md` está vazio ou
    tem itens explicitamente aceitos pelo usuário.

## Apêndice — comandos do ambiente

Comandos completos efetivamente testados estão em `docs/COMANDOS.md`.

```bash
npm run build:ui
dotnet build Lumen.sln -c Release
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
node scripts/verify-webview.mjs
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
MSYS_NO_PATHCONV=1 "/c/Program Files/Microsoft Visual Studio/18/Insiders/VSSDK/VisualStudioIntegration/Tools/Bin/CreateExpInstance.exe" /Reset /VSInstance=18.0_25e762c6 /RootSuffix=Exp
```

### Gotchas de VSIX (validados localmente em 2026-11-07)

- Mesma Id+Version **atualizou/reparou**, não foi ignorada. O script mantém
  uninstall-first e SHA-256 para comprovar o conteúdo instalado.
- `/RootSuffix Exp /UpdateConfiguration` foi necessário para o menu aparecer.
  A atualização de cache está no script e **não** equivale a reset da hive.
- `/rootSuffix:Exp /instanceIds:25e762c6` selecionam o destino do VSIXInstaller.
  Não usar `/a` (administrativo). Logs recebem nome e são gravados em `%TEMP%`.
- **`/VSInstance` de `CreateExpInstance` é o prefixo da hive, não a versão.**
  `/VSInstance=18.0` retorna exit 0 e reseta `18.0Exp` (hive não usada), deixando
  a real intacta. O comando validado é
  `…\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe /Reset
  /VSInstance=18.0_25e762c6 /RootSuffix=Exp` — 25 entradas/156 arquivos → 1/6,
  zero Lumen. No Git Bash, prefixar `MSYS_NO_PATHCONV=1`, senão `/Reset` vira
  `C:/Program Files/Git/Reset`. B6 resolvido (`BLOQUEIOS.md`).
- O manifesto empacotado e suas dependências são conferidos pelos testes.
- F5 está configurado (`VsixDeployOnDebug=true`), mas não foi exercitado;
  o teste real usou CLI e Exibir → Lumen.
- VS 2026 ignora upper bound do manifesto; o Lumen usa `[18.0,)`.

### Gotchas do VS Installer — **aprendidos na prática nesta máquina**

Três tentativas falharam antes de acertar. Não repita os erros:

1. **`--wait` NÃO EXISTE** no installer 4.12.34 → exit **87**
   (`A opção 'wait' é desconhecida`). Não use.
2. **`--passive` / `--quiet` exigem shell já elevado** → exit **5007**
   (`Commands with --quiet or --passive should be run elevated from the
   beginning`). O shell do agente **não** é elevado.
3. **`Start-Process -ArgumentList` com array perde as aspas do path** →
   `--installPath C:\Program Files\…` vira `--installPath C:\Program` → exit **1**
   (`Não é possível localizar um produto instalado`).

Forma correta — um `.ps1` com a linha de argumentos **única e pré-quotada**:

```powershell
$setup  = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe'
$vsPath = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
$argLine = 'modify --installPath "{0}" --add Microsoft.VisualStudio.Workload.VisualStudioExtension --includeRecommended --passive --norestart' -f $vsPath
$p = Start-Process -FilePath $setup -ArgumentList $argLine -Verb RunAs -Wait -PassThru
Write-Output ("SETUP_EXIT=" + $p.ExitCode)
```

**Sempre verifique o resultado de verdade**, não o exit code do wrapper:
`vswhere -prerelease -all -requires Microsoft.VisualStudio.Workload.VisualStudioExtension -property installationPath`
e o log em `%TEMP%\dd_installer_*.log` (procure `Exit Code:`).
O wrapper reportou "exit 0" mesmo quando o installer retornou 87 e 5007.

> Flags de instalação e o reset da hive confirmados e registrados em
> `docs/COMANDOS.md`. B6 resolvido em 2026-11-07 com autorização do usuário;
> a Fase 0 está encerrada.
