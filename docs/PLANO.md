# PLANO — Lumen

Plano executável para o desenvolvimento e evolução da extensão Lumen para Visual Studio Community 2026 (18.x).
Consulte [AGENTS.md](file:///c:/Users/Hanjel/Documents/Extensão/AGENTS.md) para regras de implementação, [ARQUITETURA.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/ARQUITETURA.md) para desenho técnico e [STATUS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/STATUS.md) para histórico detalhado e evidências de execução.

## Dashboard de Execução

| Fase | Objetivo | Estado | Encerrada em |
|---|---|---|---|
| **Fase 0** | Spike de toolchain e UI (VSSDK, net48, WebView2) | **CONCLUÍDA** | 2026-11-07 |
| **Fase 1** | Núcleo de domínio puro (tipos, eventos, serialização) | **CONCLUÍDA** | 2026-11-07 |
| **Fase 2** | BYOK: provedores, descoberta e capacidades | **CONCLUÍDA** | 2026-11-07 (Azure 2026-11-17) |
| **Fase 3** | UI do chat (WebView2, markdown, tool cards, streaming) | **CONCLUÍDA** | 2026-11-12 |
| **Fase 4** | Ferramentas de arquivos/shell e camada de segurança | **CONCLUÍDA** | 2026-11-12 |
| **Fase 5** | Loop do agente, modos (Agent/Ask/Plan) e orçamento | **CONCLUÍDA** | 2026-11-13 |
| **Fase 6** | Sistema de Skills (agentskills.io, progressive disclosure) | **CONCLUÍDA** | 2026-11-16 |
| **Fase 7** | Personalização visual e sincronia com tema do VS | **CONCLUÍDA** | 2026-11-16 |
| **Fase 8** | Hardening, empacotamento determinístico e release | **CONCLUÍDA** | 2026-11-17 |
| **Fase 9** | Subagentes locais e delegação com escopo restrito | **CONCLUÍDA** | 2026-11-17 |

---

## Como usar

- Execute as fases na ordem. Cada fase possui critérios de aceite verificáveis e controles negativos.
- Uma fase só termina quando: build limpo + testes verdes + **controle negativo executado** + commit + [STATUS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/STATUS.md) atualizado.
- Se bloquear: registre em [BLOQUEIOS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/BLOQUEIOS.md) com evidência (comando, saída, arquivo:linha) e siga para tarefa independente. Não invente soluções.
- Toda a narrativa histórica de execução e registros de fatias parciais residem em [STATUS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/STATUS.md).

Legenda: `[ ]` pendente · `[x]` feito · `AC` critério de aceite · `CN` controle negativo.

---

## Fases Concluídas (v0.1.0)

<details>
<summary><b>Fase 0 — Spike de toolchain e UI (CONCLUÍDA em 2026-11-07)</b></summary>

**Objetivo:** Provar o ciclo completo restore → build → instalar → abrir no VS → WebView2 renderizando o chat.

### Tarefas
- [x] Confirmar versões NuGet compatíveis e fontes (SDK 17.14.40265, BuildTools 18.5.38461, WebView2 1.0.4314.35, ReferenceAssemblies 1.0.3).
- [x] Probe net48 + WPF com SDK .NET 11 RC.
- [x] Criar Lumen.Vsix, Lumen.Core e Lumen.Tests na solução.
- [x] Manifesto clássico Community [18.0,), arquitetura amd64.
- [x] LumenToolWindow : ToolWindowPane aberta por Exibir → Lumen na instância Experimental.
- [x] WebView2 com UserDataFolder explícito em `%LocalAppData%\Lumen\WebView2`.
- [x] Assets locais empacotados, mapping `https://lumen.local/`, sem CDN.
- [x] Ponte C#↔JS versionada, tipada e com IDs de correlação; eco real validado.
- [x] Spike com markdown, highlight C#, diff lado a lado, 500 mensagens virtualizadas e composer expansível.
- [x] Troca de tema Escuro → Claro no VS atualizando CSS sem recarregar o documento.
- [x] Directory.Build.props, analyzers e runner de testes configurados.
- [x] Documentar comandos testados em COMANDOS.md.

### AC
- dotnet build -c Release produz .vsix sem erro e sem aviso.
- O .vsix instala na instância 18.12 Insiders sem erro.
- devenv /RootSuffix Exp abre e a tool window Lumen aparece.
- O WebView2 renderiza com scroll fluido, markdown, highlight e diff.
- Trocar o tema do VS atualiza o chat sem reiniciar.

### CN
- Instalação fora do range de versão é recusada pelo VSIXInstaller.
- Reinstalação de mesma Id+Version atualiza e valida SHA-256 dos assets.
- Mensagem JS malformada enviada ao C# é rejeitada com erro controlado (sem crash do VS).
- Tool window abre sem conexão de rede (assets 100% locais).

### Commit
`feat: bootstrap Lumen VSIX with verified toolchain and WebView2 chat spike`
</details>

<details>
<summary><b>Fase 1 — Núcleo de domínio (CONCLUÍDA em 2026-11-07)</b></summary>

**Objetivo:** Tipos de domínio puros, sem dependência do VS nem de HTTP.

### Tarefas
- [x] Conversation, Message, MessageRole, ContentPart (text / image / tool-call / tool-result / reasoning / unknown).
- [x] ModelInfo, ModelCapabilities, ReasoningSupport, CapabilitySource, Confidence, ReasoningKind, CapabilityOrigin.
- [x] ReasoningIntent = Off | Minimal | Low | Medium | High | Max.
- [x] StreamEvent como união discriminada (TextDelta, ReasoningDelta, ToolCallStart, ToolCallArgsDelta, ToolCallEnd, Usage, Done, StreamError).
- [x] ToolSpec, ToolResult, ToolInvocation com JSON schema estrito.
- [x] ChatMode = Agent | Ask | Plan e matriz de permissões ChatModePolicy pura.
- [x] ConversationSerializer com serialização round-trip e TryParse defensivo.
- [x] Lumen.Tests com suíte inicial xUnit (97 testes verdes).

### AC
- Lumen.Core compila em netstandard2.0 sem referenciar assemblies do VS ou System.Net.Http.
- Round-trip de serialização preserva todos os tipos de ContentPart (incluindo imagens base64 e JSON args).
- 100% dos testes de domínio aprovados sem falhas.

### CN
- JSON de conversa truncado ou corrompido falha de forma controlada (retorna false, sem exceções não tratadas).
- ContentPart desconhecido vira UnknownPart preservando payload original (não vira texto silenciosamente).
- Elementos JsonElement desserializados sobrevivem ao descarte do documento pai (.Clone() garantido).

### Commit
`feat: add Lumen core domain model and stream event contract`
</details>

<details>
<summary><b>Fase 2 — BYOK: provedores, descoberta e capacidades (CONCLUÍDA em 2026-11-07 / 2026-11-17)</b></summary>

**Objetivo:** Suporte a chaves próprias com catálogo dinâmico de modelos e detecção de visão, raciocínio e ferramentas.

### Tarefas
- [x] IChatProvider, IModelCatalog e ICredentialStore.
- [x] Adapters: OpenAI-compatível, OpenRouter, Anthropic, Gemini, Ollama, Azure OpenAI.
- [x] Descoberta de modelos sem inferência de custo (chamadas de listagem nativas).
- [x] CapabilityResolver em camadas: nativo → registry curado → heurística → probe opt-in.
- [x] capabilities.registry.json embarcado e substituível por JSON local.
- [x] Mapeamento de ReasoningIntent por provedor (omitindo campos não suportados).
- [x] Streaming normalizado para StreamEvent com remontagem de tool calls e assinaturas de pensamento.
- [x] Entrada multimodal de visão nos formatos específicos de cada provedor.
- [x] ICredentialStore via Windows Credential Manager (fallback DPAPI); zero plaintext em settings/logs.
- [x] Tratamento de erros HTTP com backoff, jitter e respeito a Retry-After.
- [x] Resolução do inventário Azure (ARM seguro + OAuth Device Code opt-in).

### AC
- Chave válida lista modelos exibindo badges de Visão, Raciocínio e Tools com origem da capacidade visível.
- Nenhuma chamada paga ocorre sem ação explícita do usuário.
- Credenciais nunca são expostas em logs, settings ou mensagens de erro.
- Testes com mocks e fixtures SSE/NDJSON sem dependência de rede.

### CN
- 401 gera erro de chave; 404 gera erro de base URL; 429 mantém chave válida e exibe tempo de espera.
- Modelo sem visão bloqueia anexos na interface e na validação.
- Modelo sem raciocínio não envia parâmetros de reasoning no payload.
- Parser de streaming sobrevive a eventos desconhecidos e pacotes truncados.
- Ferramenta com argumentos particionados em deltas múltiplos remonta integralmente.

### Commit
`feat: add BYOK providers with layered model capability discovery`
</details>

<details>
<summary><b>Fase 3 — UI do chat (CONCLUÍDA em 2026-11-12)</b></summary>

**Objetivo:** Interface de chat interativa em WebView2, fluida, virtualizada e integrada à tool window.

### Tarefas
- [x] Protocolo de mensagens C#↔JS tipado e versionado com IDs de correlação.
- [x] Histórico rolável virtualizado, composer expansível, seletores de modo, modelo e effort.
- [x] Renderização incremental de deltas de streaming diretamente no DOM.
- [x] Markdown sanitizado (markdown-it) com highlight de código e botão copiar.
- [x] Bloco de raciocínio colapsável separado da resposta final.
- [x] Tool cards com status visual (pendente, aprovando, executando, concluído, erro) e diff integrado.
- [x] Anexo de imagens com drag & drop e colar da área de transferência.
- [x] Injeção de contexto (@arquivo, @solucao, seleção ativa, logs de output).
- [x] Gerenciamento e persistência de conversas (busca, renomeação, exclusão).
- [x] Cancelamento responsivo em menos de 1 segundo para streams e ferramentas.
- [x] Acessibilidade completa (teclado, ARIA, alto contraste) e localização PT-BR / EN.

### AC
- Conversa com streaming contínuo sem flicker e sem congelamento da UI do Visual Studio.
- Cancelamento interrompe imediatamente o stream sem deixar processos ou ferramentas órfãs.
- Anexo de imagem indisponível em modelos sem suporte a visão.
- Interface funcional totalmente offline via https://lumen.local/.

### CN
- Colar 1 MB de texto no composer não trava a UI nem o IDE.
- Resposta com payloads maliciosos (<script>, <img onerror>, javascript:) é rigorosamente sanitizada.
- Mensagem JS com schema inválido ou ID de correlação desconhecido é rejeitada sem crash.
- Streaming com chunks duplicados ou fora de ordem não corrompe o DOM.

### Commit
`feat: add WebView2 chat UI with streaming, reasoning blocks and tool cards`
</details>

<details>
<summary><b>Fase 4 — Ferramentas e camada de segurança (CONCLUÍDA em 2026-11-12)</b></summary>

**Objetivo:** Ferramentas para leitura, edição/criação de arquivos e execução de comandos com aprovação estrita.

### Tarefas
- [x] WorkspaceGuard: canonicalização de paths, confinamento à raiz da solução, bloqueio de traversal e junctions externas.
- [x] Ferramentas de arquivo: read_file, find_files, list_dir, grep, create_file, edit_file, apply_diff.
- [x] Edição integrada com buffers abertos via MEF (ITextBuffer / ITextEdit) e histórico de undo do VS.
- [x] Ferramenta de terminal run_command via Process em background + Job Object Windows + output pane dedicado.
- [x] Escaping rigoroso de argumentos via listas (sem concatenação de strings com dados de modelos).
- [x] ApprovalBroker: leitura livre, escrita interna automática com diff, escrita externa com confirmação e terminal com aprovação por operação/sessão.
- [x] Blocklist de comandos destrutivos como camada de defesa em profundidade.
- [x] Ferramentas de diagnóstico: get_errors, get_output_logs, run_build, run_tests.
- [x] Validação de JSON schema de argumentos antes de autorizar qualquer execução.

### AC
- Execução encadeada de leitura, criação e edição com diff visível e undo em lote operando corretamente.
- Edição de arquivo aberto no editor reflete instantaneamente no buffer do documento.
- Processos de shell respeitam timeout e cancelamento limpando toda a árvore de subprocessos.

### CN
- read_file com caminhos relativos de escape (../../..) é bloqueado pelo guard.
- Symlinks ou junctions apontando para fora do workspace são rejeitados.
- Tentativa de escrita fora do workspace exige confirmação explícita do usuário.
- Comandos destrutivos da blocklist são barrados antes da execução.
- Parâmetros com caracteres de shell injection (;, &&, |, backticks) são tratados como argumentos literais.
- Modos Ask e Plan rejeitam comandos e escritas indevidas no nível do ApprovalBroker.

### Commit
`feat: add file and shell tools with workspace guard and approval broker`
</details>

<details>
<summary><b>Fase 5 — Loop do agente e modos (CONCLUÍDA em 2026-11-13)</b></summary>

**Objetivo:** Orquestração autônoma do agente nos modos Agent, Ask e Plan com controle de orçamento.

### Tarefas
- [x] ToolChatLoop: ciclo contínuo de chamada ao modelo, despacho de ferramentas, reinjeção e término.
- [x] Controle de orçamento e segurança via ToolBudget (rodadas, tokens e ferramentas configuráveis).
- [x] ContextBuilder unificado com system prompts específicos por modo e injeção contextual de arquivos.
- [x] Gerenciamento da janela de contexto com compactação limpa por turnos preservando pares de ferramentas.
- [x] Execução paralela de ferramentas ativada somente quando há suporte e garantias de segurança.
- [x] Modo Plan restrito à geração de planos e criação/edição exclusiva de arquivos .txt e .md.
- [x] Modo Ask estritamente somente-leitura com foco em explicações e citações.
- [x] Transição de modos durante a conversa mantendo a integridade do histórico.
- [x] Retry automático com backoff exponencial para falhas transitórias de rate-limit sem reexecutar tools já concluídas.

### AC
- Tarefas multi-etapas complexas (ex: criar classes, testes e validar com build) concluem no modo Agent.
- Modo Plan e Modo Ask nunca realizam modificações proibidas no código.
- Esgotamento de orçamento interrompe a execução com estado consistente e explicativo.
- Cancelamento a qualquer momento preserva o histórico sem deixar arquivos incompletos.

### CN
- Loop infinito de ferramentas repetitivas é interrompido pelo orçamento de segurança.
- Chamada a ferramenta inexistente devolve erro estruturado ao modelo sem quebrar o agente.
- A compactação de histórico nunca dissocia uma chamada tool_call do seu respectivo tool_result.

### Commit
`feat: add agent loop with Agent/Ask/Plan modes and context budgeting`
</details>

<details>
<summary><b>Fase 6 — Skills (CONCLUÍDA em 2026-11-16)</b></summary>

**Objetivo:** Sistema de extensibilidade modular compatível com a especificação agentskills.io.

### Tarefas
- [x] Formato padronizado de SKILL.md com frontmatter YAML e pastas estruturadas.
- [x] Descoberta em pastas do projeto (.lumen/, .github/, .claude/, .agents/) e do usuário (~/.lumen/, ~/.copilot/).
- [x] Progressive disclosure: inclusão inicial restrita a metadados, carregando o corpo completo sob demanda.
- [x] Acionamento tríplice: automático por intenção, explícito por /comando e manual via UI.
- [x] Validação estruturada de metadados com diagnóstico visual de erros.
- [x] Skills de exemplo embarcadas (revisão de código, escrita de testes unitários, explicação contextual).

### AC
- Nova skill adicionada ao projeto é detectada instantaneamente sem necessidade de reiniciar o IDE.
- Injeção contextual ocorre exclusivamente quando a skill é ativada.
- Desativação remove imediatamente qualquer instrução adicional do contexto.

### CN
- Skill sem frontmatter ou com nome duplicado acusa erro claro sem corromper as demais.
- Descrição excessivamente longa é truncada ou rejeitada para proteger o prompt do sistema.
- Tentativa de carregar arquivos de skill fora das raízes permitidas é bloqueada.

### Commit
`feat: add user-extensible Skills system with progressive disclosure`
</details>

<details>
<summary><b>Fase 7 — Personalização visual (CONCLUÍDA em 2026-11-16)</b></summary>

**Objetivo:** Sincronização automática com temas do Visual Studio e suporte a temas customizados em tempo real.

### Tarefas
- [x] Monitoramento de VSColorTheme.ThemeChanged com injeção instantânea de CSS variables no WebView2 sem reload.
- [x] Definição de temas em JSON (paleta, fontes, raios de borda, densidade e cores por modo).
- [x] Painel de visualização ao vivo com preview de alterações e botão de restauração padrão.
- [x] Mecanismo de exportação e importação de temas personalizados em arquivo JSON.
- [x] Persistência em nível de usuário em `%LocalAppData%\Lumen\providers.theme.json`.
- [x] Conformidade de contraste WCAG AA verificada nas paletas padrão.

### AC
- Alternância de tema do VS (Dark / Light / Blue / High Contrast) reflete no chat em tempo real sem piscar.
- Temas customizados persistem entre reinicializações do Visual Studio.
- Opção de reset restabelece integralmente os valores padrão do tema ativo.

### CN
- JSON de tema sintaticamente corrompido é rejeitado com aviso, mantendo o tema anterior estável.
- Avisos de contraste insuficiente são exibidos caso combinações de cores firam a legibilidade.

### Commit
`feat: add theming with VS theme sync and custom visual presets`
</details>

<details>
<summary><b>Fase 8 — Hardening, empacotamento e entrega (CONCLUÍDA em 2026-11-17)</b></summary>

**Objetivo:** Preparação para distribuição estável, build determinístico e conformidade de segurança.

### Tarefas
- [x] Auditoria geral de segurança (validação de URLs, escape de shell, sanitização e isolamento do broker).
- [x] Testes de estresse com conversas longas (200+ mensagens) e buffers extensos sem vazamento de memória.
- [x] Metadados completos de pacote no manifesto VSIX (versão 0.1.0, tags, descrição, licença MIT).
- [x] Build de Release determinístico normalizado, garantindo ausência de segredos ou artefatos temporários.
- [x] Sistema de logging estruturado seguro com níveis configuráveis e botão 'abrir log'.
- [x] Garantia de zero telemetria externa instalada por padrão.

### AC
- O arquivo .vsix resultante instala de forma limpa em instâncias do VS 2026 sem dependências externas.
- Nenhuma requisição de rede ocorre antes da configuração explícita de um provedor pelo usuário.
- Inspeção automatizada do arquivo VSIX comprova ausência total de chaves, credenciais e pastas bin/obj.

### CN
- Inicialização sem chave não emite chamadas externas.
- Varredura de logs gerados e binários compilados retorna zero ocorrências de credenciais de teste.
- Re-execução da suíte completa de controles negativos valida que todas as defesas continuam operantes.

### Commit
`chore: harden, document and package Lumen for distribution`
</details>

<details>
<summary><b>Fase 9 — Subagentes e delegação (CONCLUÍDA em 2026-11-17)</b></summary>

**Objetivo:** Permitir que o agente principal delegue subtarefas confinadas a agentes secundários locais.

### Tarefas
- [x] Contrato de delegação restrito por tipo de tarefa, modo herdado e limite de chamadas.
- [x] Descoberta de subagentes locais na pasta .lumen/agents/.
- [x] Confinamento estrito de permissões: subagentes herdam a política do modo ativo e não podem executar ações proibidas.
- [x] Painel de acompanhamento e visualização de resultados de subagentes na interface.
- [x] Integração com ModelAgent para execução de prompts especializados via provedor selecionado.
- [x] Verificação ponta a ponta na instância experimental usando provedores simulados locais.

### AC
- Tarefa delegada respeita as barreiras de modo e retorna o resultado consolidado ao chat principal.
- Erros de execução de um subagente são tratados de forma limpa sem corromper a sessão principal.
- Validação na instância experimental executada sem dependência de APIs externas pagas.

### CN
- Subagente em modo Ask ou Plan tentando acionar ferramentas de escrita ou terminal é prontamente bloqueado.
- Subagente não consegue obter credenciais brutas nem contornar os gates do ApprovalBroker.
- Tentativa de delegação recursiva infinita é barrada pelos tetos de profundidade.

### Commit
`feat: add bounded subagent delegation with inherited permissions`
</details>

---

## Trabalho Pendente (v0.1.x / Atual)

Itens críticos e melhorias ativas que demandam resolução antes de novas expansões:

### 1. B17 — pkgdef não processado no VS 2026 (CRÍTICO)
- **Problema:** O pacote VSIX instala, mas o `privateregistry.bin` não processa o `Lumen.Vsix.pkgdef`. O comando `View.Lumen` fica indisponível no menu **Exibir** em algumas condições de hive experimental.
- **Evidência:** `PkgDefSearchPath` na hive não indexa pastas de extensão não-sistema; assinatura do VSIX pode ser exigida pelo shell do VS 18.
- **Ação:** Consultar [docs/BLOQUEIOS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/BLOQUEIOS.md) para detalhes da investigação e testes com certificado/signtool.

### 2. Melhorias de UI — Fatias D e E
Conforme planejado em [docs/MELHORIAS-UI.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/MELHORIAS-UI.md):
- **Fatia D (Contexto e Custos):** Círculo indicador de percentual de contexto na barra do composer; painel de métricas da sessão (tokens consumidos, limites, modelo ativo); precificação opcional por modelo para projeção de custos.
- **Fatia E (Limpeza e Otimização):** Remoção de abas legadas não utilizadas ('Ler disco' e 'Escrever'); consolidação do botão 'Desfazer último lote' diretamente na barra de ferramentas do chat.

### 3. Consolidação de Correções Recentes (Novembro/2026)
- Estabilidade de empacotamento do arquivo binário `Menus.cto`.
- Ajuste de renderização incremental no DOM evitando `TypeError` em Text nodes.
- Suporte a nível de raciocínio `XHigh` e persistência de seleções por modelo.
- Modo automático com retomada inteligente e botão **Continuar** em substituição ao bloqueio por limite rígido.

---

## Roadmap de Evolução Futura

Funcionalidades de médio e longo prazo planejadas para versões 0.2.0+:

- **MCP Client Integrado:** Habilidade de conectar nativamente a servidores Model Context Protocol configurados na máquina ou solução.
- **Inline Completions:** Provedor de sugestões de código diretamente no editor de texto do Visual Studio.
- **Multi-File Diff Reviewer:** Visualizador unificado para inspeção de alterações em múltiplos arquivos antes de aplicar o lote.
- **Suporte Nativo a ARM64:** Adaptação e compilação de binários e manifestos para dispositivos Windows on ARM.
- **Publicação no Visual Studio Marketplace:** Resolução do item B12 (ícones, artes de apresentação e homologação pública).
- **Subagentes Autônomos Completos:** Subagentes com orçamentos independentes e modelos distintos configuráveis por papel.
- **Suporte ao Canal Estável do VS 2026:** Validação assim que a versão final RTM for disponibilizada pela Microsoft.

---

## Definition of Done Global

A extensão está pronta quando **tudo** abaixo é verdade:

1. Instala no VS Community 2026 (18.x) e abre a tool window Lumen.
2. Colar uma BYOK lista os modelos automaticamente com badges de **visão**, **raciocínio** e **tools**, mostrando a origem de cada capacidade.
3. Os três modos funcionam e respeitam a matriz de permissões.
4. A IA lê, cria e edita arquivos com diff + undo em lote, e executa comandos em cmd / PowerShell / bash — com a política de aprovação exata.
5. Skills do usuário são descobertas e acionadas com progressive disclosure.
6. Temas funcionam e seguem o tema do VS.
7. Chaves no Credential Manager; zero segredo em log, settings ou pacote.
8. Suíte de testes verde **com todos os controles negativos**.
9. Nenhuma chamada paga sem ação explícita do usuário.
10. [docs/STATUS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/STATUS.md) reflete a realidade e [docs/BLOQUEIOS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/BLOQUEIOS.md) está vazio ou tem itens explicitamente aceitos pelo usuário.

---

## Apêndice — Comandos do Ambiente

Comandos completos efetivamente testados estão em [docs/COMANDOS.md](file:///c:/Users/Hanjel/Documents/Extensão/docs/COMANDOS.md).

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

- Mesma Id+Version atualizou/reparou, não foi ignorada. O script mantém uninstall-first e SHA-256 para comprovar o conteúdo instalado.
- `/RootSuffix Exp /UpdateConfiguration` foi necessário para o menu aparecer. A atualização de cache está no script e não equivale a reset da hive.
- `/rootSuffix:Exp /instanceIds:25e762c6` selecionam o destino do VSIXInstaller. Não usar `/a` (administrativo). Logs recebem nome e são gravados em `%TEMP%`.
- **/VSInstance de CreateExpInstance é o prefixo da hive, não a versão.** Com `/VSInstance=18.0` reseta `18.0Exp` (hive vazia não usada), deixando a real intacta. O comando validado é `CreateExpInstance.exe /Reset /VSInstance=18.0_25e762c6 /RootSuffix=Exp`.
- O manifesto empacotado e suas dependências são conferidos pelos testes.
- VS 2026 ignora upper bound do manifesto; o Lumen usa `[18.0,)`.

### Gotchas do VS Installer — aprendidos na prática nesta máquina

1. **`--wait` NÃO EXISTE** no installer 4.12.34 → exit 87. Não use.
2. **`--passive` / `--quiet` exigem shell já elevado** → exit 5007. O shell do agente não é elevado.
3. **`Start-Process -ArgumentList` com array perde as aspas do path** → `--installPath C:\Program Files\...` vira `--installPath C:\Program` → exit 1.

Forma correta — um `.ps1` com a linha de argumentos **única e pré-quotada**:

```powershell
$setup  = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe'
$vsPath = 'C:\Program Files\Microsoft Visual Studio\18\Insiders'
$argLine = 'modify --installPath "{0}" --add Microsoft.VisualStudio.Workload.VisualStudioExtension --includeRecommended --passive --norestart' -f $vsPath
$p = Start-Process -FilePath $setup -ArgumentList $argLine -Verb RunAs -Wait -PassThru
Write-Output ("SETUP_EXIT=" + $p.ExitCode)
```
