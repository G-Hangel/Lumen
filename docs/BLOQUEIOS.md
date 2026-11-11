# BLOQUEIOS — Lumen

Bloqueios, pré-requisitos e armadilhas conhecidas. Ao resolver um item, mova-o
para "Resolvidos" com a evidência. Ao encontrar um novo, registre com comando,
saída e `arquivo:linha` — **não invente solução**.

---

## Abertos

### B9 — Captura CDP após regressão automática readonly (2026-11-10)

`node scripts/verify-auto-tools-webview.mjs verify` atingiu as asserções de
reinjeção/replay, opt-out, rejeições, limite de loop, cancelamento, solução
fechada e layout, mas expirou em `Page.captureScreenshot` (30 s), em
`scripts/verify-auto-tools-webview.mjs:199`. Uma captura isolada do mesmo estado
com `fromSurface:false`/`captureBeyondViewport:false` também expirou (15 s).
Causa ainda não identificada; não houve replay dos turnos para obter a imagem.

Saída: `Error: Page.captureScreenshot`, no evento do timer (`:69`). Evidência:
saídas das tarefas `bdomve6jh` e da captura isolada na sessão. A imagem e o relatório
automáticos anteriores não contam como evidência de aprovação integral desta
regressão. Asserção de exceções JS (`:201`) não foi atingida. Cleanup removeu
apenas provedor/chave/conversas próprios; fixture e Exp fechadas, pacote verificado.

Não bloqueia a fatia manual 4c: seu próprio verificador completo, imagem, controle
negativo e 0 exceções JS passaram no pacote final. Não declarar a regressão
automática integralmente concluída até resolver a captura e fechar seu relatório.

### B8 — Azure: inventory de deployments requer Entra/RBAC

A rota data-plane prevista não foi confirmada pela documentação oficial.
`GET /openai/v1/models` valida a chave, mas lista ofertas, não nomes de deployments.
A listagem ARM exige token Entra/RBAC; chave BYOK do recurso é insuficiente.
O adapter exige nomes informados e retorna `deployment_required` se faltarem.
Fontes e rota ARM exata em `PESQUISA.md` §1.1; descoberta automática permanece limitada.
**Status:** aceito como limitação pelo usuário em 2026-11-07 ao instruir o avanço
para a Fase 3; deployments manuais funcionam. Não bloqueia as próximas fases.

### B2 — Disco com pouco espaço

`C:` tinha **19 GB livres** antes do workload; `df -h /c` em 2026-11-07 mostrou
**16 GB livres**, 94% ocupado. Builds de VSIX, cache NuGet e a hive experimental
consomem espaço, mas o build limpo da Fase 0 passou.

**Mitigação:** antes de build grande, conferir `df -h /c`. Se necessário, limpar
`bin/`, `obj/` e o cache NuGet (`dotnet nuget locals all --clear`). Não apagar
nada do usuário sem perguntar.

### B3 — Insiders expira em 2027-01-26

A instância de teste é prerelease com data de expiração. Depois disso o ambiente
de teste morre.

**Mitigação:** manifesto com range `[18.0,)` (não `[18.12,)`), e testar em VS 18
estável quando disponível. Não prender o projeto ao Insiders.

### B4 — `gh` CLI ausente

Não há CLI do GitHub, então não dá para criar remote nem abrir PR
automaticamente.

**Mitigação:** o repositório é **local**. Criar remote e dar push é decisão e
ação do usuário (`AGENTS.md` §6). Não tentar contornar.

---

## Resolvidos

### B7 — Ollama real: AC dispensado pelo usuário (2026-11-07)

O executável/processo Ollama não existia na máquina (checagem por `Get-Command`/
`Get-Process` sem resultado). Em 2026-11-07 o usuário declarou na conversa:
"não precisa fazer o ollama local, eu já fiz os testes e o chat realmente funciona"
— teste com chave real feito pelo próprio usuário na UI, com chat funcionando.
O AC de Ollama local fica **dispensado por decisão explícita do usuário**; o
adapter continua coberto por mocks/fixtures. Nenhum gasto foi feito pelo agente.

### B6 — Reset da hive experimental validado (2026-11-07, com autorização)

O usuário autorizou o reset nesta sessão. Comando validado:

```
"C:\Program Files\Microsoft Visual Studio\18\Insiders\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe" /Reset /VSInstance=18.0_25e762c6 /RootSuffix=Exp
```

**`/VSInstance` é o prefixo da hive, não a versão.** Com `/VSInstance=18.0` o
utilitário resetou `18.0Exp` (hive vazia e não usada) e avisou
`o diretório do Visual Studio …\18.0 não existe`, deixando a hive real
**intacta**: 25 entradas, 156 arquivos, Lumen ainda em
`Extensions\4un2n5ic.1yv`. Esse é o **controle negativo** — prova que o comando
documentado no plano não fazia nada e que a verificação detecta a diferença.

Com `/VSInstance=18.0_25e762c6`, saída: `…\18.0_25e762c6Exp removido`,
`…\Extensions criado`, `Diretório Extensões copiado de …\18.0_25e762c6\Extensions`.
Estado depois: **1 entrada, 6 arquivos, zero vestígio de Lumen**
(`find -iname "*lumen*"` vazio).

**Ciclo completo pós-reset re-exercitado na hive nova:** build Release
0 erros/0 avisos → 35 testes C# + 17 testes JS verdes → `Install` com SHA-256
de cada arquivo em `Extensions\nrqagayt.p4k` → **Exibir → Lumen** abriu a tool
window → `node scripts/verify-webview.mjs` exit 0 com `layoutNegative: true`,
10 requests todos em `https://lumen.local`, `exceptions: []` → `Verify` exit 0.

**Controle negativo do detector de menu:** com Lumen desinstalado,
`scripts/open-experimental.ps1 -Action Open` falhou com exit 1 e
`The Lumen command is missing from the Exibir menu`, encerrando o VS.
O mesmo script, após reinstalar, deu exit 0.

`CreateExpInstance.exe` **não** fica em `Common7\IDE`; fica em
`VSSDK\VisualStudioIntegration\Tools\Bin`. Flags confirmados por `/?`:
`/Create`, `/Reset`, `/Clean`, `/VSInstance=<VsInstance>`, `/RootSuffix=<Suffix>`.

### B1 — Workload de desenvolvimento de extensão instalado

`vswhere -prerelease -all -requires Microsoft.VisualStudio.Workload.VisualStudioExtension -property installationPath`
confirmou `C:\Program Files\Microsoft Visual Studio\18\Insiders`.
Componentes `VisualStudioExtension` e `VSSDK` presentes.
`%TEMP%\dd_installer_20261106215357.log`: `Exit Code: 0`; warnings de rede não
foram fatais. O wrapper COM reportou `SETUP_EXIT=-2146233029`, mas o log e o
estado instalado confirmaram sucesso. Não é pré-requisito do restore clássico.

### B5 — Versões NuGet compatíveis fixadas

SDK 17.14.40265 + BuildTools 18.5.38461 + WebView2 1.0.4314.35 +
ReferenceAssemblies 1.0.3 restauraram e compilaram juntas, zero erros/avisos.
BuildTools 18.9.820 está unlisted e não foi escolhido. Fontes em `PESQUISA.md` §2.7.

### R1 — `--wait` não existe no VS Installer → exit 87

**Sintoma:** `setup.exe modify … --passive --norestart --wait` retornou 87.
**Evidência:** `%TEMP%\dd_installer_20261106180123.log` —
`A opção 'wait' é desconhecida.`
**Causa:** o installer 4.12.34 não tem a opção `--wait`.
**Fix:** remover `--wait`. Para esperar, usar `Start-Process -Wait` do PowerShell.

### R2 — `--passive` exige shell já elevado → exit 5007

**Sintoma:** mesmo comando sem `--wait`, rodado do shell do agente, retornou 5007.
**Evidência:** log — `Commands with --quiet or --passive should be run elevated
from the beginning.` e `vs.willow.iselevated : False`.
**Causa:** o shell do agente não é elevado, embora o usuário seja admin
(`isadmin: True`).
**Fix:** `Start-Process -Verb RunAs` ( dispara UAC ).

### R3 — `Start-Process -ArgumentList` com array trunca o path → exit 1

**Sintoma:** com `-Verb RunAs`, o installer retornou 1:
`Não é possível localizar um produto instalado que corresponda aos parâmetros`.
**Evidência:** `dd_installer_20261106180610.log` linha 6 —
`Parsed command line options: … --installPath C:\Program …`
(o path foi cortado no primeiro espaço).
**Causa:** `-ArgumentList` recebendo **array** junta com espaços e **perde as
aspas** dos elementos que contêm espaço.
**Fix:** passar **uma string única pré-quotada**:
`-ArgumentList ('modify --installPath "{0}" …' -f $vsPath)`.

### R4 — Exit code do wrapper não é confiável

**Sintoma:** a notificação de background task disse "exit code 0" quando o
installer havia retornado **87** e depois **5007**.
**Causa:** o `echo "EXITCODE=$?"` final do comando Bash sucedia, mascarando o
código real; e o wrapper reporta o exit do shell, não do processo filho.
**Fix:** sempre capturar o código dentro do próprio script
(`$p.ExitCode` com `-PassThru`) **e** verificar o estado real
(`vswhere -requires …`, log `Exit Code:`, espaço em disco).
**Lição generalizada:** never trust a wrapper's exit code for a child process.

### R5 — Medo de que o SDK .NET 11 RC não compilasse TFM antigo

**Sintoma:** só .NET SDK 11.0.100-rc.3 instalado; nenhuma versão 8/9/10.
**Verificação:** probe real — classlib `net8.0-windows` com `UseWPF=true`
compilou com **0 erros, 0 avisos, 8,77 s**.
**Conclusão:** down-level targeting funciona; os targeting packs restauram do
NuGet. Probe removida depois.
**Atualização 2026-11-07:** probe `net48` + WPF + ReferenceAssemblies 1.0.3
passou com zero erros/avisos em 49,12 s (`artifacts/net48-probe.binlog`).
O build limpo da solução também passou; nenhum SDK adicional foi necessário.

### R6 — Incerteza sobre o runtime do VS 2026

**Dúvida:** "VS 2026 migrou para .NET" — isso mudaria o TFM da extensão.
**Verificação local:** `Common7/IDE/devenv.exe.config` declara
`<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2" />`;
não existe `devenv.runtimeconfig.json` nem `coreclr.dll` no IDE.
**Conclusão:** o **shell continua em .NET Framework**. A migração para .NET vale
para componentes **out-of-process** (o VS traz hosts .NET 8.0.33 e 10.0.14 em
`dotnet/net{8,10}.0/runtime`). Confirmado pela doc da Microsoft.
**Implicação:** VSSDK clássico `net48` é válido e suportado no 18.x.

---

### R7 — VSIX instalado, mas comando ausente do menu

O pacote estava habilitado e seus arquivos conferiam, mas Exibir não continha
Lumen. `devenv /RootSuffix Exp /UpdateConfiguration` forçou regeneração do cache
(`artifacts/vs-update.xml`); depois **Exibir → Lumen** abriu a janela.
`scripts/deploy-experimental.ps1` incorpora essa etapa após install/uninstall.
Não apaga hive e não altera o perfil principal.

### R8 — Dependências `System.*` omitidas do VSIX

O teste do pacote falhou por ausência de `System.Text.Json.dll`. O filtro padrão
do VSSDK omitia DLLs de framework, apesar de CopyLocal. O target
`IncludeRuntimeDependencies` em `src/Lumen.Vsix/Lumen.Vsix.csproj` inclui runtime;
as referências ao SDK do VS usam `ExcludeAssets="runtime"`.
`VsixPackageTests` compara DLLs do output com o ZIP e rejeita remoções/mutações.

### R9 — Unicode inválido escapava da validação JSON

`dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Surrogate`
reproduziu **2 falhas e 1 sucesso** antes da correção.
`BridgeProtocol.cs:56`: `InvalidOperationException` ao decodificar `\uD800` ou
`\uDC00`. `JsonDocument.Parse` aceita o token, mas strings/nomes têm validação
tardia. O protocolo retorna `invalid_message` também nessa fronteira; testes
cobrem valores, nomes de campos e par válido.

### R10 — Layout estreito cortava a área rolável

O verificador real detectou `overlap: true` em 203×422 com composer multilinha.
O tamanho fixo subtraído do cabeçalho de `details` foi substituído por grid/flex;
a altura mínima permite rolagem externa quando o painel é muito baixo.
O teste força `min-width:240px` em viewport 203 para provar que detecta overflow.

### R11 — Construtor de desserialização do System.Text.Json exige tipos idênticos

**Sintoma:** `TryParse` devolvia `invalid_json` para **toda** conversa válida,
inclusive as produzidas pelo próprio serializador.
**Evidência:** `InvalidOperationException: Each parameter in the deserialization
constructor on type 'Lumen.Core.Domain.Conversation' must bind to an object
property or field on deserialization.`
**Causa:** o construtor recebia `DateTimeOffset?` enquanto a propriedade é
`DateTimeOffset`. Nullable não casa; o STJ rejeita o construtor inteiro.
**Fix:** parâmetros com o tipo exato da propriedade e `default` como sentinela de
"carimbar agora" (`Conversation.cs`).
**Lição:** `TryParse` que engole exceção esconde a causa real — para diagnosticar,
chamar `JsonSerializer.Deserialize` diretamente num teste temporário e imprimir a
exceção, em vez de chutar.

### R12 — `JsonElement` retido precisa de `.Clone()`

**Sintoma:** `ObjectDisposedException: Cannot access a disposed object.
Object name: 'JsonDocument'` ao serializar uma conversa recém-desserializada.
**Causa:** `ContentPartConverter.Read` usa `using var document =
JsonDocument.ParseValue(ref reader)`. Os `JsonElement` extraídos dali morrem com o
`document`; só o `UnknownPart` clonava, os argumentos de `ToolCallPart` não.
**Fix:** `.Clone()` em todo elemento que sobrevive ao método (`ContentPart.cs`).
**Controle negativo:** o teste
`DeserializedJsonElementsOutliveTheirBackingDocument` foi escrito para isso e
**falha** com `ObjectDisposedException` quando o `.Clone()` é removido —
confirmado revertendo a correção (1 falha) e restaurando (0 falhas).

---

### R13 — Revisão BYOK: identidade, replay, EOF e evidência (2026-11-07)

Controle negativo reproduzido no código nativo:

```bash
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --filter 'FullyQualifiedName~ConfigurationGuidCasing|FullyQualifiedName~PaidRequestReplay|FullyQualifiedName~RemovedConfigurationCannot|FullyQualifiedName~GeminiUnsignedThenSigned'
```

Antes: **4 falhas / 1 sucesso**. Depois: **5 sucessos**. GUIDs são canônicos
(`Contracts.cs:24`), a deduplicação não apaga IDs antigos após 1024 requests
(`ByokSession.cs:70`), remover configuração invalida cache sem devolver orçamento
(`ByokSession.cs:153`) e Gemini conserva limites dos Parts assinados
(`ByokSession.cs:242`). EOF sem `message_stop`/`finishReason` produz `invalid_stream`
(`AnthropicProvider.cs:407`, `GeminiProvider.cs:295`) e não grava histórico parcial.

O probe passou a retornar inconclusivo em HTTP 200 (`CapabilityHttpProbe.cs:83`);
negativo permanente exige declaração incondicional do modelo, não `tool_choice`,
restrição condicional ou parâmetro genérico (`CapabilityHttpProbe.cs:121`). Cancel
cobre RPCs não-chat (`wwwroot/byok.mjs:102`) e reconcilia orçamento só pela sessão
nativa (`wwwroot/byok.mjs:285`). Testes usam apenas mocks/chaves sintéticas.

### R14 — Erro de catálogo anunciava modelos carregados

O WebView2 mostrou mensagem de sucesso ao lado de 401/404, embora nenhum modelo
fosse carregado. Novo controle em `scripts/byok-ui-tests.mjs:403` reproduziu **5
falhas** antes da correção. `applyCatalog` usa estado pronto, não sucesso de
listagem, quando há erro e a credencial não foi validada; 429 autenticado mantém
seu estado distinto. Catálogo público mantém a mensagem de chave não verificada.

### R15 — Verificador acessava o DOM antes do handshake (2026-11-07)

Após build/testes/install verdes, `node scripts/verify-webview.mjs` falhou com
`TypeError: Cannot read properties of null (reading 'dataset')`. A tool window
WPF já estava aberta, mas o documento ainda não tinha o elemento de status.
`scripts/verify-webview.mjs:45` e `scripts/verify-byok-webview.mjs:45` agora esperam
por `connected` via MutationObserver com prazo de 10 segundos, sem polling ou
handshake simulado. Reexecução dos dois scripts no WebView2 real: exit 0, sem
exceções e com controle negativo de layout detectado.

### R16 — Onda 3d: contrato `retryAfter` host↔JS e build quebrado (2026-11-09)

A auditoria da 3d encontrou a árvore no meio de uma edição que quebrava build e
testes. Achados corrigidos com evidência:

- **`retryAfter:null` rejeitado pelo validador JS (bug latente).** `JsonWire.Serialize`
  usa `JsonSerializer.Serialize` padrão, que **serializa nulos**; o host sempre envia
  `retryAfter` no evento `Error` e no catálogo (`null` quando o provedor não manda o
  header). O validador de `Error` em `wwwroot/byok-bridge.mjs` exigia inteiro, então
  **todo erro de stream real viraria `invalid_response`**. Corrigido para aceitar
  `null`/inteiro≥0, alinhado aos validadores de catálogo/probe.
- **Build C# quebrado (3× CS1503 `double?`→`long?`).** `RetryAfter(response)?.TotalSeconds`
  em `AnthropicProvider.cs:192`, `GeminiProvider.cs:180`, `OpenAIProvider.cs:150`.
  Novo `HttpTransport.RetryAfterSeconds` (`Math.Ceiling`→`long?`, nunca sub-reporta)
  substitui os três; `ByokSession` CatalogView/EventView usam o mesmo teto.
- **`ByokError` posicional.** O ctor `(code, message, retryAfter)` recebia `retryAfter`
  no slot de `message` (`byok-bridge.mjs:228`); assinatura corrigida para `(code, retryAfter)`.
- **Verde enganoso.** `dotnet test --no-build` validou binários obsoletos, não o código
  editado; só o build fresco expôs os erros. Lição: rebuild antes de afirmar verde.
- **Teardown de fixture JS.** `t.after` roda em FIFO no `node:test`; com vários fixtures
  no mesmo teste, o primeiro restore deletava `document` e os disposes seguintes
  falhavam. `uiFixture` agora registra um único teardown que desfaz em ordem inversa.

Controle negativo provado por mutação: desativar o retry (`attempt == 0`) fez
`Catalog429SurfacesTheLastResponseRetryAfterNotTheFirst` falhar (`Expected:3, Actual:1`)
antes de reverter. Pós-correção: build 0/0, **1.121 C# + 160 JS** verdes e regressão
completa na Exp (429 surfaced `retryAfter:90`, 3 tentativas, 0 exceções).

## Armadilhas que NÃO são bloqueios (mas vão morder)

- **Git Bash converte `/Reset` em `C:/Program Files/Git/Reset`** ao chamar
  `.exe` do Windows. Erro real: `Parâmetro inválido 'C:/Program Files/Git/Reset'`.
  Prefixar com `MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'`, ou chamar de
  PowerShell.
- **`/VSInstance=18.0` não é a hive do Insiders.** Usar `18.0_<instanceId>`.
  O utilitário avisa quando o diretório base não existe — ler a saída, não só o
  exit code (que foi **0** no reset da hive errada).
- **VSIX mesma Id+Version atualizou/reparou** no instalador 18.12 local,
  contrariando a hipótese anterior: `artifacts/lumen-same-version.log:725,1071–1134`.
  O deploy mantém uninstall-first; o controle com `app.mjs` adulterado foi
  rejeitado com `Stale installed file: wwwroot/app.mjs`.
- Range `[99.0,)` foi recusado com exit **2003**, `NoApplicableSKUsException`:
  `artifacts/lumen-unsupported.log:26–36`.
- `/instanceIds` é plural; `/logFile` aceita nome e grava em `%TEMP%`.
  `/a` é instalação administrativa, não instalação na Exp.
- **`obj\...\extension.vsixmanifest` obsoleto** pode ser empacotado no lugar do
  novo. Limpe `obj/` ao mudar o manifesto.
- **WebView2 renderiza em branco** sem `UserDataFolder` explícito.
- **Deadlock na UI thread** com `.Result` / `.Wait()` em código in-process.
  Usar `async`/`await` + `JoinableTaskFactory`.
- **Não existe API pública de terminal no VS.** Usar `Process` + output pane.
  Não tentar `Microsoft.VisualStudio.Terminal.dll` (interno).
- **APIs `Copilot.Internal.*` / `UnstableInternalApi.*` e a API emergente
  `ICopilot*Provider` são proibidas** — sem contrato público.
- **Probing de capacidades gasta crédito do usuário.** Opt-in, orçado, só sobre
  o modelo selecionado. Nunca ao colar a chave.