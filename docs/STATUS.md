# STATUS — Lumen

Atualizado em **2026-11-07**.

## Estado atual

**Fases 0, 1 e 2 concluídas; Fase 3 em andamento — fatia 3a concluída em
2026-11-07.** A Fase 2 foi fechada com limites explicitamente aceitos pelo
usuário em 2026-11-07: ele testou com chave real na UI (listagem e chat
funcionaram), dispensou o Ollama local (B7) e aceitou o inventory Azure manual
(B8). Nenhum remote nem push foi realizado.

| Fase | Estado |
| --- | --- |
| Planejamento | concluído |
| Fase 0 — Spike de toolchain e UI | **concluído** |
| Fase 1 — Núcleo de domínio | **concluído** |
| Fase 2 — BYOK | **concluído (limites aceitos pelo usuário)** |
| Fase 3 — UI do chat | **em andamento — 3a concluída; 3b–3d pendentes** |
| Fases 4–8 | não iniciadas |

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
  `artifacts/phase3a.binlog`, `artifacts/net48-probe.binlog`.
- `artifacts/tests/phase0.trx`, `artifacts/tests/phase2.trx`,
  `artifacts/tests/phase3a.trx`.
- `artifacts/webview-verification.json`, `artifacts/webview-offline.png`.
- `artifacts/webview-theme.json`, `artifacts/byok-webview.json`,
  `artifacts/byok-webview.png`.
- `artifacts/vs-activity.xml`, `artifacts/vs-update.xml` e logs `lumen-*.log`.

O preview em navegador nunca simula conexão C#: sem host, enviar fica
indisponível. Teste de produto foi feito no WebView2 do VS, não apenas no preview.
Nenhum serviço real de IA, credencial real ou crédito pago foi utilizado.

Estado ao fim da Fase 0: hive `18.0_25e762c6Exp` resetada, Lumen em
`Extensions\nrqagayt.p4k`, Exp fechada e servidores da sessão encerrados.
O estado atual da Fase 2 está registrado nas evidências acima; o perfil
principal do VS permanece intocado.

## Próximo passo exato

**Fase 3 — fatia 3b (persistência de conversas), aguardando autorização do
usuário.** Histórico persistente com busca/renomear/excluir e virtualização do
log (o limite atual de 20 turnos é provisório da 3a). Depois 3c (contexto
`@arquivo`, `@solução`, seleção do editor) e 3d (acessibilidade final), cada
fatia com seu commit. A 3a foi autorizada e concluída em 2026-11-07.

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