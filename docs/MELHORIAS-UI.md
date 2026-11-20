# Melhorias de UI — plano em fatias

Documento de trabalho para a próxima rodada de otimizações do Lumen.
Cada fatia é implementada e commitada separadamente, com build, testes e
controles negativos verdes antes de seguir para a próxima.

Decisões do usuário (travadas):

- Ordem de implementação: **A → B → C → D → E**.
- Modo Plan pode **criar e editar apenas arquivos `.txt`/`.md`**; nunca executa
  comandos. (Altera o escopo travado em `AGENTS.md`; atualizar na fatia B.)
- Troca de modelo em sessão: **mesma sessão com guardas** — bloqueia com
  mensagem clara se o novo modelo não suportar tools/visão já usados.
- Permissões: Interativo = comportamento atual; **Permissivo = só dispensa
  aprovação de escrita fora do workspace** (terminal sempre pede, exceto em
  Acesso Total); Acesso Total = nada pede, mas blocklist e confinamento de path
  permanecem como defesa em profundidade.
- "Desfazer último lote" vai para a **toolbar do chat**.

---

## Fatia A — Composer (itens 4, 5 e 8) — concluída

1. **Effort junto do modelo.** O seletor `byok-effort` sai do painel "Opções de
   envio" e passa para a linha "Ponte conectada" (`composer-meta`), ao lado do
   modelo ativo. Opções vindas de `model.efforts`; desabilitado quando o modelo
   não tem reasoning.
2. **Imagem por arrastar/colar.** Removidos o input de arquivo e o painel
   "Opções de envio". A imagem entra por drag & drop no composer ou colando no
   textarea. Mantida a validação (PNG/JPEG/GIF/WebP, 4 MB, magic bytes) e o chip
   com "Remover imagem". Drop de não-imagem ou arquivo grande → erro localizado.
3. **Prompt e Enter.** Removido o limite de 4096 (textarea, UI, bridge e host);
   novo teto de **262144** caracteres (mesmo teto de contexto do contrato),
   validado nas fronteiras. **Enter envia; Shift+Enter quebra linha** (respeita
   IME/`isComposing`; com sugestões de skill abertas, Enter escolhe a sugestão).
   Hint, `aria-keyshortcuts` e contador atualizados.

Critérios de aceite / controles negativos:

- Enter não envia com send desabilitado nem durante composição IME; Shift+Enter
  nunca envia.
- Prompt de 4097+ caracteres aceito (até 262144); 262145 rejeitado pela UI e
  pelas fronteiras bridge/host (teste negativo nos dois lados).
- Drop de `.txt` ou arquivo grande rejeitado com código localizado; colar imagem
  válida anexa e envia com `image` no payload.
- Esforço selecionado chega no `byok.chat`; seletor reflete o modelo novo.
- Suítes verde (UI + .NET + pacote), build 0/0, VSIX reconstruído, commit local.

## Fatia B — Permissões por modo (item 3) — concluída

- Remover os checkboxes "ferramentas automáticas" e o painel de ajuda.
- `readTools/writeTools/terminal` passam a derivar do modo **no host**: Ask =
  ler/listar; Agent = ler, criar, editar, terminal; Plan = ler + criar/editar
  `.txt`/`.md`.
- Novo seletor de permissão ao lado do modo: **Interativo** (atual),
  **Permissivo** (só escrita fora do workspace pede aprovação), **Acesso Total**
  (nada pede; blocklist e paths continuam).
- Atualizar `AGENTS.md` (escopo do Plan) e a matriz de testes do host/bridge/UI.

## Fatia C — Modelo e abas de sessão (itens 1 e 2) — concluída

- Seletor de modelo na linha do composer, ligado ao provedor ativo; troca com
  guardas de capacidade sem descartar a conversa.
- Abas de sessão entre topbar e chat (título + modelo); abrir = `byok.history.open`,
  nova = `byok.history.new`. Persistir lista + aba ativa em
  `%LOCALAPPDATA%\Lumen\providers.ui.json` e restaurar ao abrir o Lumen.
- Histórico continua como lista completa para retomar qualquer conversa.

## Fatia D — Contexto e custos (itens 6 e 9)

- Círculo com % de contexto no composer; clique abre painel com nome da sessão,
  limite (`contextWindowTokens`), uso %, criada em, modelo atual e custo total.
- Preço opcional por modelo (input/output por 1M tokens) no painel Provedores;
  acumular tokens por sessão no `Conversation`; painel lista modelos, preço e
  uso; custo total só aparece quando o preço foi informado.
- Remover "Ajuda e aviso de custo".

## Fatia E — Limpeza (item 7)

- Remover abas "Ler disco" e "Escrever" (nav + workspace).
- Realocar "Desfazer último lote" e status de pendências para a toolbar do chat.
- Atualizar testes de UI, localizações e `docs/STATUS.md`.
### Evidência (Fatia A)

- Build Release: 0 erros / 0 avisos.
- `npm run check:ui`: **236/236**.
- `dotnet test`: **1667/1667** (novo `LongPromptsPastLegacyLimitAreAcceptedAndArchived`).
- `npm run check:package`: **3/3**.
- Controles negativos: Enter não envia durante composição IME, com send desabilitado ou com Shift; prompt de 262145 rejeitado na UI e nas fronteiras (bridge/host); drop de não-imagem e arquivo grande rejeitados; troca de modelo bloqueada quando a sessão usa ferramentas e o novo modelo não as suporta.
- `SameSelectionRetainsConversationButDifferentModelOrConfigurationClearsIt` agora cobre os dois caminhos: troca de modelo preserva a sessão (replay no novo modelo), troca de provedor ainda cria sessão nova.
### EvidÃªncia (Fatia B)

- Build Release: 0 erros / 0 avisos.
- npm run check:ui: 236/236.
- dotnet test: 1668/1668 (inclui PlanWritesOnlyPlanDocuments, PermissionLevelsOnlyRelaxAgentApprovals, PlanModeCanCreatePlanDocuments).
- npm run check:package: 3/3.
- Controles negativos: a ponte rejeita terminal/writeTools legados fora do modo (Ask/Plan); Plan nÃ£o anuncia run_command; WorkspaceWriteTools rejeita .cs e apply_diff em Plan (disco e buffers abertos); ApprovalBroker mantÃ©m mode_denied para escrita fora do workspace em Plan e comandos em Ask/Plan; Permissivo ainda pede aprovaÃ§Ã£o para comandos; Acesso Total nunca pede, mas blocklist e confinamento de path permanecem.
- AGENTS.md atualizado: Plan cria/edita apenas .txt/.md; o seletor de permissÃ£o sÃ³ relaxa aprovaÃ§Ãµes.
### Evidência (Fatia B)

- Build Release: 0 erros / 0 avisos.
- npm run check:ui: 236/236.
- dotnet test: 1668/1668 (inclui PlanWritesOnlyPlanDocuments, PermissionLevelsOnlyRelaxAgentApprovals, PlanModeCanCreatePlanDocuments).
- npm run check:package: 3/3.
- Controles negativos: a ponte rejeita terminal/writeTools legados fora do modo (Ask/Plan); Plan não anuncia run_command; WorkspaceWriteTools rejeita .cs e apply_diff em Plan (disco e buffers abertos); ApprovalBroker mantém mode_denied para escrita fora do workspace em Plan e comandos em Ask/Plan; Permissivo ainda pede aprovação para comandos; Acesso Total nunca pede, mas blocklist e confinamento de path permanecem.
- AGENTS.md atualizado: Plan cria/edita apenas .txt/.md; o seletor de permissão só relaxa aprovações.
- VSIX reconstruído e instalado nos dois hives (57/57 arquivos verificados por SHA-256 em cada um).
### Evidência (Fatia C)

- Build Release: 0 erros / 0 avisos.
- npm run check:ui: 238/238.
- dotnet test: 1669/1669 (inclui SwitchingToModelWithoutToolsIsRejectedWhenTheSessionUsedTools).
- npm run check:package: 3/3.
- Controles negativos: troca de modelo bloqueada quando a sessão já usou tools/visão e o novo modelo não suporta; bloqueio também no byok.chat (defesa em profundidade); abas persistidas em localStorage (apenas a chave lumen.sessionTabs.v1); fechar aba não apaga conversa; provider removido fecha abas daquele provedor.
- VSIX reconstruído e instalado nos dois hives; 57/57 arquivos verificados por SHA-256 em cada um.

## Melhorias de providers e esforço de raciocínio (2026-11-20)

- Effort XHigh adicionado (minimal, low, medium, high, xhigh, max).
- Configuração por modelo: nome de exibição, tools, reasoning e níveis de esforço, persistidos em providers.models.json.
- Botão de remover provedor virou ícone de lixeira com confirmação.
- Controles negativos: displayName > 128 e efforts fora da lista global rejeitados.
