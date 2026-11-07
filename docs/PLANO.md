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

- [ ] `Conversation`, `Message`, `MessageRole`, `ContentPart`
      (text / image / tool-call / tool-result / reasoning).
- [ ] `ModelInfo`, `ModelCapabilities` (flags de `ARQUITETURA.md` §2.4),
      `ReasoningSupport`, `CapabilitySource`, `Confidence`.
- [ ] `ReasoningIntent = Off | Minimal | Low | Medium | High | Max`.
- [ ] `StreamEvent` como união discriminada:
      `TextDelta, ReasoningDelta, ToolCallStart, ToolCallArgsDelta, ToolCallEnd,
      Usage, Done, Error` (`PESQUISA.md` §1.9).
- [ ] `ToolSpec` / `ToolResult` / `ToolInvocation` com JSON schema.
- [ ] `ChatMode = Agent | Ask | Plan` e a matriz de permissões de
      `ARQUITETURA.md` §2.3.
- [ ] Serialização round-trip de conversa em JSON (para persistir histórico).
- [ ] `Lumen.Tests` com xUnit, cobertura dos tipos e da serialização.

### AC

- `Lumen.Core` compila sem referenciar **nenhuma** assembly do VS ou HTTP.
- Round-trip de serialização preserva todos os tipos de `ContentPart`,
  incluindo imagem base64 e argumentos de tool call.
- Testes verdes.

### CN

- JSON de conversa **malformado / truncado** deve falhar de forma controlada,
  não corromper o histórico nem lançar exceção não tratada.
- `ContentPart` desconhecido deve ser preservado ou descartado explicitamente —
  nunca silenciosamente virar texto.

### Commit

`feat: add Lumen core domain model and stream event contract`

---

## Fase 2 — BYOK: provedores, descoberta e capacidades

**Objetivo:** colar uma chave e ver a lista de modelos com badges corretos de
visão / raciocínio / tools. **Esta é a feature-assinatura do produto.**

### Tarefas

- [ ] `IChatProvider` + `IModelCatalog` + `ICredentialStore`.
- [ ] Adapters, nesta ordem:
      1. **OpenAI-compatível** (cobre OpenAI, DeepSeek, Groq, Mistral, xAI,
         Together, Cerebras, vLLM, llama.cpp, LM Studio) — base URL configurável.
      2. **OpenRouter** — metadado mais rico, melhor para validar a UI.
      3. **Anthropic** — `capabilities` nativo.
      4. **Gemini**.
      5. **Ollama** — local, sem chave, ótimo para testes sem custo.
      6. **Azure OpenAI** — deployments, não model ids.
- [ ] Descoberta de modelos exatamente por `PESQUISA.md` §1.1–1.5.
      Validação de chave **pela própria chamada de listagem** (custo zero).
- [ ] `CapabilityResolver` em camadas (`PESQUISA.md` §1.6):
      nativo → registry → heurística → probe opt-in.
- [ ] `capabilities.registry.json` embarcado, atualizável sem release.
      Cada entrada com `source`, `confidence`, `verifiedAt`.
- [ ] Mapeamento de `ReasoningIntent` por provedor (`PESQUISA.md` §1.7`),
      **omitindo o campo** quando não suportado.
- [ ] Streaming normalizado para `StreamEvent` (`PESQUISA.md` §1.9`),
      incluindo remontagem de tool-call deltas e preservação de
      `signature` / `thoughtSignature` para replay.
- [ ] Entrada de visão nos três formatos (`PESQUISA.md` §1.8`), com validação
      de tamanho antes de enviar.
- [ ] `ICredentialStore` via **Windows Credential Manager**
      (`PESQUISA.md` §1.10`), fallback DPAPI.
- [ ] Tratamento de erro por código HTTP conforme a tabela de §1.10,
      com backoff + jitter honrando `retry-after`.
- [ ] UI de configuração: adicionar provedor, colar chave, testar, listar
      modelos com badges e **origem da capacidade** visível.
- [ ] Fixtures SSE gravadas para cada provedor em `Lumen.Tests/Fixtures/`.

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

- [ ] Protocolo de mensagens tipado e versionado entre C# e JS, com ids de
      correlação para pedido/resposta. Validar **toda** mensagem recebida.
- [ ] Layout: histórico rolável virtualizado, composer multilinha, barra de modo
      (Agent/Ask/Plan), seletor de modelo, seletor de effort.
- [ ] Renderização incremental de streaming — aplicar delta no DOM, **não**
      re-renderizar a mensagem inteira a cada chunk.
- [ ] Markdown (markdown-it, `html:false`) + code block com highlight
      (highlight.js) e botão copiar.
- [ ] Bloco de **raciocínio colapsável**, separado da resposta.
- [ ] **Tool cards**: nome, argumentos, estado (pendente/aprovando/executando/
      concluído/falhou), resultado expansível, diff embutido.
- [ ] Anexo de imagem (drag-drop, colar, arquivo) — habilitado só se o modelo
      tem `Vision`.
- [ ] Contexto: `@arquivo`, `@solução`, seleção ativa do editor, output window.
- [ ] Histórico de conversas persistente, busca, renomear, excluir.
- [ ] Cancelamento visível e responsivo durante stream e durante tool.
- [ ] Estados de erro acionáveis (chave inválida → abrir settings;
      429 → mostrar tempo de espera).
- [ ] Acessibilidade: navegação por teclado, ARIA, screen reader, contraste.
- [ ] Localização PT-BR e EN (resources no C# + dicionário no JS).
- [ ] CSP restritiva no `index.html`; assets 100% locais.

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

### Tarefas

- [ ] **Workspace guard**: canonicalização + confinamento por prefixo,
      rejeição de traversal, symlink para fora, UNC não autorizado.
- [ ] File tools: `read_file` (com range de linhas), `find_files` (glob),
      `list_dir`, `grep`, `create_file`, `edit_file`, `apply_diff`.
- [ ] `edit_file` com **diff visível** e registro de undo; **undo em lote**
      reverte o turno inteiro.
- [ ] Edição em arquivo **aberto no editor** deve sincronizar com o buffer:
      MEF `ITextBuffer.CreateEdit()` / `ITextEdit.Apply()`, com acesso à UI thread
      via `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync`.
      Undo real do VS, não só arquivo em disco (`ARQUITETURA.md` §2.5).
- [ ] Shell tools: `run_command` com detecção de cmd / powershell / pwsh /
      bash (Git Bash), captura de stdout+stderr+exit code, timeout,
      cancelamento, e leitura de output em background.
      **Não existe API pública de terminal no VS** — usar
      `System.Diagnostics.Process` + pane próprio no Output Window
      (`IVsOutputWindowPane`). Não tentar usar
      `Microsoft.VisualStudio.Terminal.dll` (serviço interno, não suportado).
- [ ] **Nunca** concatenar string para montar comando. `ProcessStartInfo`
      com argumentos escapados corretamente + caminho do executável explícito.
      Todo o trabalho de processo roda **fora da UI thread**.
- [ ] `ApprovalBroker` implementando `AGENTS.md` §1:
      leitura livre · edit dentro da solução automático · fora do workspace
      confirma · **todo** comando confirma com "sempre permitir nesta sessão".
- [ ] Blocklist de destrutivos como defesa em profundidade.
- [ ] Tools de diagnóstico: `get_errors` (Error List), `get_output_logs`,
      `run_build`, `run_tests` se acessível.
- [ ] Toda tool com JSON schema validado **antes** da execução.

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

### Tarefas

- [ ] `AgentLoop`: monta contexto → chama provider → executa tools → realimenta
      → repete até concluir.
- [ ] Orçamento: máx de iterações, máx de tokens, máx de tools por turno.
      Configurável. Ao estourar, para e explica — não continua silenciosamente.
- [ ] `ContextBuilder`: system prompt por modo, seleção do editor, @arquivos,
      skills ativas, estrutura da solução.
- [ ] **Context window management**: contagem de token, truncamento/compactação
      de histórico antigo preservando tool calls pendentes.
- [ ] Paralelismo de tools só se o modelo tem `ParallelTools`.
- [ ] Modo **Plan**: produz plano em fases com critérios de aceite;
      oferece "aplicar plano" que muda para Agent.
- [ ] Modo **Ask**: read-only, respostas com citações `arquivo:linha`.
- [ ] Troca de modo no meio da conversa preservando histórico.
- [ ] Retry com backoff em falha transitória, sem duplicar tool já executada.

### AC

- Tarefa real de múltiplos passos (ex.: "crie uma classe X com teste e faça
      build") conclui no modo Agent com verificação.
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

- [ ] Formato: pasta com `SKILL.md` + frontmatter YAML (`name`, `description`),
      arquivos de apoio opcionais. **Adotar o padrão agentskills.io**, que é o
      mesmo do Agent Skills nativo do VS 2026 (desde 18.5) — assim as skills que
      o usuário já tem funcionam sem conversão (`ARQUITETURA.md` §2.10).
- [ ] Discovery em `.lumen/skills/`, `.github/skills/`, `.claude/skills/`,
      `.agents/skills/` (projeto) e `~/.lumen/skills/`, `~/.copilot/skills/`
      (usuário). Precedência: projeto > usuário; no projeto,
      `.lumen/` > `.github/` > `.claude/` > `.agents/`.
- [ ] **Progressive disclosure**: só `name` + `description` no system prompt;
      corpo carregado sob demanda quando acionada.
- [ ] Acionamento: automático por descrição, explícito por `/nome`, e manual
      pela UI.
- [ ] Skill pode declarar ferramentas próprias e restringir modos.
- [ ] UI: listar, ativar/desativar, criar, editar, abrir a pasta, recarregar.
- [ ] Validação de frontmatter com erro acionável.
- [ ] Algumas skills de exemplo embarcadas (ex.: revisão de código, escrever
      testes, explicar seleção).

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

- [ ] Seguir o tema do VS (dark / light / high contrast) por padrão, reagindo ao
      evento `VSColorTheme.ThemeChanged` e reinjetando as CSS variables no
      WebView2 — **sem reload** da página.
- [ ] Temas próprios em JSON, materializados como **CSS variables**: cores,
      fonte do chat, fonte de código, tamanho, densidade, raio de borda,
      cor de destaque por modo.
- [ ] Preview ao vivo antes de aplicar; reset para o padrão.
- [ ] Importar/exportar tema.
- [ ] Persistência por usuário, não por solução.
- [ ] Verificar contraste WCAG AA nos temas embarcados.

### AC

- Trocar o tema do VS muda o chat na hora, sem reiniciar.
- Tema custom aplicado e persistido entre sessões.
- Reset volta ao padrão.

### CN

- Tema JSON inválido → **rejeitado com erro**, não deixa a UI ilegível.
- Tema com contraste insuficiente → **aviso** na UI.
- Combinação fonte/tamanho extrema não pode quebrar o layout.

### Commit

`feat: add theming with VS theme sync and custom visual presets`

---

## Fase 8 — Hardening, empacotamento e entrega

**Objetivo:** extensão instalável e confiável por outra pessoa.

### Tarefas

- [ ] Revisão de segurança: paths, shell injection, sanitização de markdown,
      vazamento de chave, permissões do broker.
- [ ] Teste de carga: conversa longa (200+ mensagens), muitos arquivos,
      output de comando grande. Sem leak de memória.
- [ ] Teste em VS 18 **estável** se disponível, além do Insiders.
- [ ] Ícone, preview image, display name, descrição, tags de marketplace.
- [ ] `LICENSE`, `README` de usuário final, `CHANGELOG`.
- [ ] Guia de instalação e de primeiro uso (colar chave → escolher modelo →
      conversar).
- [ ] VSIX de Release determinístico; verificar que nenhuma chave, `bin/`,
      `obj/` ou segredo entrou no pacote.
- [ ] Diagnóstico: log estruturado **sem segredos**, nível configurável,
      botão "abrir log".
- [ ] Telemetria: **nenhuma** por padrão. Se houver, opt-in explícito.

### AC

- O `.vsix` instala em uma máquina limpa com VS 18 e funciona sem configuração
  além da chave.
- Nenhuma chamada de rede acontece antes de o usuário configurar um provedor.
- Inspeção do pacote: zero segredos, zero `bin/obj`.
- Todos os testes verdes, todos os CN das fases anteriores ainda passando.

### CN

- Instalar e abrir **sem nenhuma chave configurada**: a extensão deve abrir,
  mostrar o onboarding e **não fazer nenhuma requisição de rede**.
- Procurar a chave de teste em todo o conteúdo do `.vsix` e nos logs gerados →
  **zero** ocorrências.
- Rodar a suíte inteira de CN de segurança uma última vez → tudo ainda rejeita.

### Commit

`chore: harden, document and package Lumen for distribution`

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