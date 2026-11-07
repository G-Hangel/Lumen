# STATUS — Lumen

Atualizado em **2026-11-07**.

## Estado atual

**Fase 0 e Fase 1 concluídas.** A Fase 0 terminou com o reset da hive validado
(B6 resolvido com autorização do usuário). A Fase 1 adicionou o núcleo de domínio
puro em `Lumen.Core`, sem VS e sem HTTP, e o VSIX continuou instalando e abrindo
depois do rebuild. Nenhum remote nem push foi realizado.

| Fase | Estado |
| --- | --- |
| Planejamento | concluído |
| Fase 0 — Spike de toolchain e UI | **concluído** |
| Fase 1 — Núcleo de domínio | **concluído** |
| Fases 2–8 | não iniciadas |

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

- `artifacts/phase0.binlog`, `artifacts/phase1.binlog`, `artifacts/net48-probe.binlog`.
- `artifacts/tests/phase0.trx`.
- `artifacts/webview-verification.json`, `artifacts/webview-offline.png`.
- `artifacts/webview-theme.json`.
- `artifacts/vs-activity.xml`, `artifacts/vs-update.xml` e logs `lumen-*.log`.

O preview em navegador nunca simula conexão C#: sem host, enviar fica
indisponível. Teste de produto foi feito no WebView2 do VS, não apenas no preview.
Nenhuma API de IA, credencial ou crédito pago foi utilizado.

Estado deixado nesta máquina: hive `18.0_25e762c6Exp` resetada e com Lumen
instalado e verificado em `Extensions\nrqagayt.p4k`; nenhuma instância Exp aberta;
nenhum servidor de preview rodando; perfil principal do VS intocado.

## Próximo passo exato

**Fase 2 — BYOK: provedores, descoberta e capacidades** (`PLANO.md`): criar
`Lumen.Providers`, `IChatProvider` / `IModelCatalog` / `ICredentialStore`, os
adapters e o `CapabilityResolver` em camadas de `PESQUISA.md` §1.6, mais
`capabilities.registry.json` embarcado. Preencher `ModelInfo.Origins` com a
origem **por capacidade**, que é o que a UI mostra como badge.

**Limite de gasto:** a Fase 2 toca APIs pagas. Descoberta de modelos usa
endpoints de listagem (OpenRouter é público; os demais exigem chave). Nenhuma
chamada real sem chave de teste fornecida pelo usuário **e** autorização por
escrito; até lá, tudo com fixtures gravadas e mocks. Probing ativo continua
opt-in, orçado e só sobre o modelo selecionado.

Não iniciar a Fase 2 sem o commit da Fase 1 estar feito.

## Decisões e limites

Lumen é chat próprio independente, VSSDK clássico `net48` + WPF/WebView2.
Aprovações, ausência de dependência de Copilot e política de credenciais continuam
as de `AGENTS.md`. A autorização desta sessão cobriu comandos locais, caches e o
reset da hive **experimental**; nunca cobriu alterar o perfil principal do VS,
criar remote, dar push ou usar APIs pagas.

Para retomar: `AGENTS.md` → `PLANO.md` → `ARQUITETURA.md` → `PESQUISA.md` →
este arquivo e `BLOQUEIOS.md`; comandos reproduzíveis em `COMANDOS.md`.