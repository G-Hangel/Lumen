# STATUS — Lumen

Atualizado em **2026-11-09**.

## Estado atual

**Fases 0, 1 e 2 concluídas; Fase 3 em andamento — fatias 3a, 3b, 3c e 3d
concluídas; 3d verificada em 2026-11-09.** A Fase 2 foi fechada com limites explicitamente aceitos pelo
usuário em 2026-11-07: ele testou com chave real na UI (listagem e chat
funcionaram), dispensou o Ollama local (B7) e aceitou o inventory Azure manual
(B8). Nenhum remote nem push foi realizado.

| Fase | Estado |
| --- | --- |
| Planejamento | concluído |
| Fase 0 — Spike de toolchain e UI | **concluído** |
| Fase 1 — Núcleo de domínio | **concluído** |
| Fase 2 — BYOK | **concluído (limites aceitos pelo usuário)** |
| Fase 3 — UI do chat | **em andamento — 3a, 3b, 3c e 3d concluídas** |
| Fases 4–8 | não iniciadas |

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

**Fase 3 — fatia 3d (acessibilidade final) concluída e verificada em 2026-11-09;
aguardando autorização para o próximo passo.**
A 3c (`@arquivo`, `@solução`, seleção do editor e Output) foi autorizada e
verificada no VS em 2026-11-08; a 3d (acessibilidade + estados de erro acionáveis)
foi autorizada e verificada em 2026-11-09. Não avançar para a Fase 4 sem nova
autorização. Itens da Fase 3 que dependem de ferramentas (tool cards,
cancelamento durante tool) ficam para a Fase 4. A 3a foi autorizada e concluída
em 2026-11-07; a 3b foi autorizada e verificada em 2026-11-08. O histórico não
tem mais o limite provisório de 20 turnos da 3a.
Nenhum remote, push ou commit das fatias 3b/3c/3d foi realizado.

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