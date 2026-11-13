# COMANDOS — Fases 0–4c e readonly automático

Testados em **2026-11-07–10**, na raiz `C:\Users\Hanjel\Documents\Extensão`.
VS Community Insiders 18.12.12328.396, instanceId `25e762c6`, SDK .NET
11.0.100-rc.3.26522.148, Node.js 24, WebView2 Runtime 156.0.4314.58.

**Escopo:** todos os comandos abaixo foram executados e verificados, incluindo o
reset destrutivo da hive de teste (autorizado pelo usuário em 2026-11-07).
Nenhum deles toca o perfil principal do VS nem usa API paga.

## Dependências, build e testes (Git Bash)

```bash
npm ci
npm run build:ui
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release -bl:artifacts/phase0.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase0.trx' --results-directory artifacts/tests
```

Resultado: build limpo **0 erros/0 avisos**, **35 testes C#** e **17 testes JS**
verdes, com controles negativos. O SDK emite a mensagem informativa NETSDK1057
por ser preview. `npm ci` avisou sobre postinstall do esbuild não aprovado;
não foram alteradas permissões e o bundle funcionou com o binário já fornecido
pelo pacote de plataforma.

Pacote produzido: `src/Lumen.Vsix/bin/Release/net48/Lumen.Vsix.vsix`.
A suíte C# de empacotamento exige esse VSIX Release: fazer build antes do teste.
Os assets e licenças são locais; nenhuma API paga é usada.

## Deploy somente na instância experimental (Git Bash)

Fechar a instância **Exp**, sem matar processos nem encerrar o VS principal.
O script rejeita deploy enquanto a instância experimental selecionada estiver
aberta; essa rejeição também foi observada no teste.

```bash
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Uninstall -InstanceId 25e762c6
```

As três ações foram testadas. `Install` remove uma instalação anterior de Lumen,
instala o pacote, atualiza o cache e compara SHA-256 de cada arquivo instalável.
`Uninstall` remove somente a identidade Lumen; não reseta nem apaga a hive.
A instalação final foi deixada disponível na Exp.

O script descobre a instalação com vswhere e exige VS 18. Usa o VSIXInstaller
**daquela instalação**, `/q /rootSuffix:Exp /instanceIds:25e762c6` e
`/uninstall:Lumen.37b13e92-66f4-4ebd-8f31-4fe1a889f684` para remover.
Não usa `/a`, `/shutdownprocesses` ou flags de elevação. `/logFile` recebe um
nome; o installer grava em `%TEMP%`, e o script copia o log para `artifacts/`.

`[Content_Types].xml` é metadado OPC e não é instalado. Todos os demais arquivos
do pacote são comparados. Um VSIX de teste com `wwwroot/app.mjs` adulterado foi
rejeitado pelo verificador; o pacote adulterado não foi instalado.

## Abrir e fechar o VS experimental (Git Bash)

```bash
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
```

Testado. O script descobre a instalação com vswhere, lança
`devenv /RootSuffix Exp /Log artifacts\vs-activity.xml`, dispensa a tela inicial
(**Continuar sem código**) e abre **Exibir → Lumen** por UIAutomation — o caminho
real de abertura, não `/Command` nem F5. Falha com mensagem explícita se o menu
ou a tool window não aparecerem, e encerra o VS que lançou. `Close` usa
`WindowPattern.Close()` e confirma que nenhum `devenv` Exp sobreviveu.

A hive correta é `%LocalAppData%\Microsoft\VisualStudio\18.0_25e762c6Exp`.

## Reset da hive experimental (autorizado e testado)

Com a Exp fechada. **`/VSInstance` é o prefixo da hive** (`18.0_<instanceId>`),
não a versão `18.0`:

```bash
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "/c/Program Files/Microsoft Visual Studio/18/Insiders/VSSDK/VisualStudioIntegration/Tools/Bin/CreateExpInstance.exe" /Reset /VSInstance=18.0_25e762c6 /RootSuffix=Exp
```

Saída observada: `…\18.0_25e762c6Exp removido` · `…\Extensions criado` ·
`Diretório Extensões copiado de …\18.0_25e762c6\Extensions`. A hive passou de
**25 entradas / 156 arquivos** para **1 / 6**, sem nenhum arquivo com "lumen" no
nome. Exit **0**.

`CreateExpInstance.exe` fica em `VSSDK\VisualStudioIntegration\Tools\Bin`,
**não** em `Common7\IDE`. Flags confirmados com `/?`: `/Create`, `/Reset`,
`/Clean`.

**Controle negativo:** `/VSInstance=18.0` também retorna exit **0**, mas reseta a
hive errada (`18.0Exp`, não usada) e avisa
`o diretório do Visual Studio …\18.0 não existe`. A hive real continuou com
25 entradas, 156 arquivos e Lumen instalado. **Exit code não basta** — conferir a
contagem de arquivos da hive e a ausência de Lumen.

Depois do reset é preciso reinstalar (`deploy-experimental.ps1 -Action Install`),
que já roda `/UpdateConfiguration`. O ciclo completo pós-reset foi revalidado:
build limpo, 35 testes C# + 17 JS verdes, install verificado por SHA-256 em
`Extensions\nrqagayt.p4k`, tool window aberta e `verify-webview.mjs` exit 0.

## Atualizar o cache experimental (PowerShell)

Com a Exp fechada:

```powershell
$p = Start-Process 'C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\devenv.exe' -ArgumentList '/RootSuffix Exp /UpdateConfiguration /Log "C:\Users\Hanjel\Documents\Extensão\artifacts\vs-update.xml"' -Wait -PassThru
$p.ExitCode
```

Exit **0**. Antes disso, a extensão estava instalada, mas Lumen não aparecia no
menu. Depois da regeneração do cache, o comando abriu a tool window.
O script de deploy já executa essa etapa. **Isso não é reset da hive.**

## Teste real do WebView2

`open-experimental.ps1` já lança a Exp com diagnóstico **somente em loopback**
(`--remote-debugging-port=9223 --remote-debugging-address=127.0.0.1`), definido
apenas naquele processo — a variável não é gravada nas configurações do sistema.
Abrir **Exibir → Lumen** é automático; aguardar **Ponte conectada** e executar:

```bash
node scripts/verify-webview.mjs
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
```

Esse comando conecta ao WebView2 real, não a um mock. Na Fase 3a o eco foi
removido; o verificador foi atualizado para o chat unificado. Verifica:

- Handshake real; chat vazio com estado inicial, envio desabilitado sem modelo.
- **Controle negativo:** submit forjado sem modelo selecionado não cria turno
  nem esconde o estado vazio.
- Mensagem inválida e surrogate isolado rejeitados pelo C#; ping posterior
  ainda responde (controles negativos da ponte).
- Reload com cache desabilitado e offline via CDP, sem desligar a rede da máquina.
- Todos os requests de UI permanecem em `https://lumen.local`.
- Layouts emulados 203×422, 320×320, 375×700, 900×900 sem overflow/sobreposição;
  overflow forçado detectado.

Salva `artifacts/webview-verification.json` e `artifacts/webview-offline.png`.
Restaura emulação de tamanho/rede ao terminar. O processo de diagnóstico deve
ser fechado ao final; a porta não faz parte do produto nem é habilitada no código.

Tema foi testado por **Ferramentas → Tema → Claro** no VS: evento
`theme.changed`, fundo `#282828` → `#F9F9F9`, mesmo `performance.timeOrigin` e
ponte conectada. Evidência em `artifacts/webview-theme.json`, coletada na hive
**anterior ao reset**; o teste de tema não foi repetido na hive nova, pois depende
de troca manual de tema do VS e não de estado da hive. Todo o resto acima foi
re-executado pós-reset.

## Preview no navegador (não substitui o VS)

```bash
npm run serve:ui
```

Abrir `http://127.0.0.1:4173/index.html?culture=pt-BR`.
Sem C#, a UI mostra preview e desabilita envio; não fabrica um handshake.
O servidor aceita somente loopback, GET/HEAD e paths confinados a `wwwroot`.

## Controles negativos de instalação

- VSIX de teste com lower bound `[99.0,)`: recusado pelo instalador real com
  exit **2003**, `NoApplicableSKUsException` (`artifacts/lumen-unsupported.log`).
- Reinstalar a mesma Id+Version **atualizou/reparou** nesta versão do installer,
  em vez de ignorar. O deploy continua uninstall-first e verificado por hash.
- VSIX com `wwwroot/app.mjs` adulterado: rejeitado por
  `Stale installed file: wwwroot/app.mjs`; não foi instalado.
- Deploy com a Exp aberta: o script recusa e não mata processo.
- `CreateExpInstance /Reset /VSInstance=18.0`: exit 0, hive real intacta —
  controle negativo do reset, detalhado na seção de reset.
- `open-experimental.ps1 -Action Open` com Lumen desinstalado: exit 1 e
  `The Lumen command is missing from the Exibir menu`.

O estado ao fim da Fase 0 foi hive `18.0_25e762c6Exp` resetada e Lumen em
`Extensions\nrqagayt.p4k`, Exp fechada e perfil principal intocado. A instalação
foi substituída pelos builds das fases seguintes.

## Fase 2 — build e verificação BYOK offline

```bash
npm run build:ui
dotnet restore Lumen.sln
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release -bl:artifacts/phase2.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase2.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Em outro processo local autorizado:

```bash
node scripts/byok-fixture-server.mjs
```

O servidor escuta apenas `127.0.0.1:4184`, responde com fixtures e não encaminha
chaves nem chama APIs externas. O caso Tools não-streaming tem atraso sintético
para testar cancelamento; o sinal `/fixture/wait-probe` evita polling.

Com a Exp nova e o servidor em execução:

```bash
node scripts/verify-webview.mjs
node scripts/verify-byok-webview.mjs
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

Build limpo: **0 erros/0 avisos**, **813 testes C#** e **91 testes JS** verdes,
com controles negativos. O verificador BYOK usa a ponte C# do WebView2 real,
configura somente chave sintética, prova que o catálogo público não valida a
chave, bloqueia anexo sem visão, anexa PNG real, recebe streaming/tool card e
exercita consentimento, deduplicação e cancelamento do probe. Remove ao final
somente configurações/credenciais que criou. Não substitui integração com
Ollama real nem com uma chave BYOK real.

Evidências ignoradas pelo Git: `artifacts/phase2.binlog`,
`artifacts/tests/phase2.trx`, `artifacts/byok-webview.json`,
`artifacts/byok-webview.png` e o relatório de regressão do spike.
Encerre somente o servidor de fixtures e a Exp lançados por esta sessão;
nenhum processo preexistente nem perfil principal precisa ser alterado.

## Fase 3a — chat unificado (2026-11-07)

Mesmo ciclo da Fase 2, com `phase3a.binlog`/`phase3a.trx`:

```bash
npm run build:ui
dotnet build Lumen.sln -c Release -bl:artifacts/phase3a.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase3a.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
node scripts/byok-fixture-server.mjs   # em outro processo local
node scripts/verify-webview.mjs
node scripts/verify-byok-webview.mjs
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

Resultado: build **0 erros/0 avisos**, **813 testes C#** e **93 testes JS**
verdes; install SHA-256 em `Extensions\5avnx5ua.xdx`; ambos os verificadores
exit 0 com `exceptions: []` e `layoutNegative: true`. O chat agora é único:
o eco local foi removido e o composer envia ao provedor selecionado no painel
BYOK (configurações). Opções de esforço/imagem e a ajuda com aviso de custo
ficam em `<details>` colapsados; a nota de conexão some quando conectado.
O verificador BYOK ganhou o modelo `fixture/code` (roteado por `payload.model`)
para exercitar o botão copiar com clipboard real: exige `Page.bringToFront` +
`Emulation.setFocusEmulationEnabled` porque o WebView2 sem foco do SO rejeita
`writeText` com `NotAllowedError` mesmo com permissão concedida. O orçamento
de probe é por sessão nativa: reiniciar a Exp antes do verificador é obrigatório
para começar em 5/5. Nenhuma API paga foi chamada; as três configurações
reais do usuário na Exp foram preservadas.

## Fase 3b — histórico persistente (2026-11-08)

```bash
npm run build:ui
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release -bl:artifacts/phase3b.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase3b.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Aguardar **Ponte conectada** antes dos verificadores. Em outro processo,
`node scripts/byok-fixture-server.mjs`; não usa serviços externos. Com a Exp
recém-aberta e orçamento 5/5, executar os verificadores de regressão antes da 3b:

```bash
node scripts/verify-webview.mjs
node scripts/verify-byok-webview.mjs
node scripts/verify-history-webview.mjs prepare
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Novamente aguardar **Ponte conectada**, então:

```bash
node scripts/verify-history-webview.mjs resume
node scripts/verify-history-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

Encerrar apenas o processo de fixtures criado nesta execução. `prepare` cria
um manifesto em `artifacts/history-fixtures.json` **antes** das gravações e recusa
sobrescrevê-lo; numa execução interrompida, usar `cleanup` com a Exp aberta antes
de começar outro `prepare`. A limpeza atua somente nos IDs desse manifesto e
remove suas configurações/credenciais; não apaga conversas arbitrárias. Os
verificadores de regressão também removem somente as conversas que criaram.
`verify-webview.mjs` exige uma interface recém-aberta (painel fechado, chat vazio);
se outro verificador deixou o painel aberto, reiniciar a Exp antes dele.

Resultado: build limpo **0 erros/0 avisos**, **982 testes C# / 121 JS** verdes,
sem ignorados. Install SHA-256 em `Extensions\1tzvqmde.asw`. Histórico de 500
turnos com alturas variáveis montou 4 itens no início e 8 no fim. Nova conversa
não apagou o arquivo; rename preservou conteúdo; exclusão sem confirmação foi
recusada; confirmação explícita removeu a conversa. Retomada após reinício enviou
3 mensagens, excluindo o par cancelado de um arquivo com 3 turnos. Operações de
histórico mantiveram os contadores HTTP das fixtures inalterados.

Evidências: `artifacts/phase3b.binlog`, `artifacts/tests/phase3b.trx`,
`artifacts/history-webview.json`, `artifacts/history-webview.png`,
`artifacts/webview-verification.json`, `artifacts/byok-webview.json`.
Todos os relatórios reais terminaram sem exceções JS e com controles negativos.

Dados em `%LocalAppData%\Lumen\conversations\<guid>.json`, não criptografados;
chaves continuam no cofre nativo. Limites: 64 MiB / 10.000 turnos por arquivo,
256 resultados na lista, 10 turnos por página, cache de 40 no navegador. Página
serializada acima de 4 Mi caracteres dá erro controlado, sem truncar o arquivo.
Busca e leitura do host ainda percorrem arquivos completos; paginação limita a
ponte e o DOM, não o IO interno. Em janelas baixas, a página tem rolagem vertical.

## Fase 3c — contexto explícito do editor/solução (2026-11-08)

Na raiz do repositório, com Exp fechada:

```bash
npm run check:ui
node --check scripts/verify-context-webview.mjs
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release --no-restore -bl:artifacts/phase3c.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase3c.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Em outro processo, iniciar `node scripts/byok-fixture-server.mjs`. Depois de
**Ponte conectada**, regressão em uma Exp recém-aberta:

```bash
node scripts/verify-webview.mjs
node scripts/verify-byok-webview.mjs
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-context-webview.mjs
node scripts/verify-context-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

Encerrar somente o servidor de fixtures iniciado nesta execução. O verificador
usa apenas loopback e uma configuração com credencial sintética; nenhuma chave
real é necessária. A fixture PowerShell encontra DTE no ROT pelo PID exato da
Exp, recusa substituir solução aberta e escreve seu manifesto antes de criar
um diretório aleatório em `artifacts/context-<guid>`. Usa o assembly público
`Microsoft.VisualStudio.Interop` 17.14.40260 `lib/net20` restaurado pelo projeto
para um helper C# de DTE2 compatível com Windows PowerShell.

`Prepare` cria projeto net48 sem restore NuGet, buffer não salvo, seleção na
linha 2 e pane Output sintético. `verify-context-webview.mjs` fecha essa solução
sem salvar para testar stale. `cleanup` remove somente os IDs de conversa e
configuração/credencial do manifesto; `Cleanup` PowerShell remove somente a
pasta própria e o manifesto. Nunca limpar conversas arbitrárias.

Em execução interrompida, consultar `-Action Inspect`; usar `-Action Initialize`
para completar apenas a fixture existente (abre somente sua solução quando não
há outra aberta). Não repetir `Prepare` sobre um manifesto existente. Para
limpeza com solução própria ainda aberta, `-Action Close` a fecha sem salvar;
rodar o cleanup JS antes de fechar a Exp. Manifestos são lidos como UTF-8 explícito
para preservar o caminho do repositório com “ã”.

**Resultado final:** clean/build **0 erros/avisos**, **1.115 testes C# / 151 JS**
verdes. Arquivo com caminho explícito e vazio capturou o buffer não salvo, não
o disco; seleção `Sample.cs:2-2`, inventário sem conteúdo e Output confirmados.
Referência `@solução`, prévia/remover e envio foram usados no WebView2. O hash
SHA-256 do contexto no request coincidiu com o texto persistido e aberto no
histórico. Traversal, ADS, tamanho, binário e solução fechada rejeitados; captura
/stale não chamaram o provedor. Verificação final: **0 probes, 0 serviços pagos,
0 exceções JS**. Junction real exercitada; symlink de arquivo indisponível por
permissão, portanto esse controle específico não foi exercitado.

Install/Verify comparou todos os arquivos em `Extensions\azw1lxof.mmi`. Exp e
servidor próprios fechados; fixture removida; três configurações originais e
seus modelos preservados; arquivos de conversa sintéticos e probe cache vazios.
Evidências: `artifacts/phase3c.binlog`, `artifacts/tests/phase3c.trx`,
`artifacts/phase3c-js.log`, `artifacts/context-webview.json`,
`artifacts/context-webview.png`, `artifacts/webview-verification.json`,
`artifacts/byok-webview.json`.

## Fase 3d — acessibilidade e estados de erro acionáveis (2026-11-09)

Na raiz do repositório, com Exp fechada:

```bash
npm run build:ui
npm run check:ui
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release --no-restore -bl:artifacts/phase3d.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase3d.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Em outro processo, iniciar `node scripts/byok-fixture-server.mjs`. Depois de
**Ponte conectada**, com a Exp recém-aberta (painel fechado, chat vazio, orçamento
5/5), regressão que combina as fatias 3b (histórico) e 3c (contexto):

```bash
node scripts/verify-webview.mjs
node scripts/verify-byok-webview.mjs
node scripts/verify-history-webview.mjs prepare
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Aguardar **Ponte conectada** de novo, então:

```bash
node scripts/verify-history-webview.mjs resume
node scripts/verify-history-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-context-webview.mjs
node scripts/verify-context-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

Encerrar somente o servidor de fixtures desta execução. O verificador BYOK
estendido exige o fixture offline: `/error429/models` conta tentativas por caminho
e devolve `Retry-After` **0, 0, 90** (módulo 3), para que as duas retentativas do
host sejam instantâneas e o terceiro response — o que volta ao chamador — carregue
90. O loop de statuses roda **[429, 401, 404]**, terminando em `invalid_url`, para
que o `errorActionFocus` foque `byok-url`. Ele asserta `retryAfter===90`,
`rateLimitAttempts===3` e a mensagem de espera localizada; o acessibilidade confere
skip link, `role="list"`, live region e `aria-keyshortcuts` no DOM real.

**Resultado final:** clean/build **0 erros/0 avisos**, **1.121 testes C# / 160 JS**
verdes. O controle negativo foi provado por mutação: desativar o retry fez
`Catalog429SurfacesTheLastResponseRetryAfterNotTheFirst` falhar
(`Expected:3, Actual:1`) antes de reverter. Na Exp, o 429 surfaced `retryAfter:90`
com "aguardar 90 s" (pt-BR), `credentialValid:true` e 3 tentativas; o botão de erro
abriu o painel e focou `byok-url`; skip link/atalhos/live region confirmados.
Histórico retomou 3 turnos após reinício (replay 3 mensagens, exclusão confirmada);
contexto capturou buffer não salvo, seleção `Sample.cs:2-2`, inventário e Output,
rejeitando traversal/tamanho/binário/stale. **0 probes, 0 serviços pagos,
0 exceções JS** em todos os relatórios.

Install/Verify comparou todos os arquivos em `Extensions\nyyefehw.l21`. Exp e
servidor próprios fechados; fixtures removidas por IDs do manifesto; três
configurações originais e seus modelos preservados; conversas sintéticas e probe
cache vazios; preview 4173 intocado. Evidências: `artifacts/phase3d.binlog`,
`artifacts/tests/phase3d.trx`, `artifacts/byok-webview.json`,
`artifacts/byok-webview.png`, `artifacts/history-webview.json`,
`artifacts/history-webview.png`, `artifacts/context-webview.json`,
`artifacts/context-webview.png`, `artifacts/webview-verification.json`.

## Fase 4b — ferramentas readonly explícitas (2026-11-09)

Com a Exp fechada, build e testes sem provedores externos:

```bash
npm run build:ui
npm run check:ui
dotnet build Lumen.sln -c Release -bl:artifacts/phase4b.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=phase4b.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Aguardar **Ponte conectada**; regressão exige painel fechado/chat vazio. Não
iniciar servidor BYOK, não configurar modelo/chave e não repetir `Prepare` se
já existir manifesto. A fixture recusa substituir solução alheia.

```bash
node scripts/verify-webview.mjs
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-read-tools-webview.mjs verify
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
node scripts/verify-read-tools-webview.mjs closed
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`Prepare` inclui texto HTML inerte, binário e arquivo de 1 MiB + 1 byte na pasta
própria. `verify` exercita RPC/formulário real, disk versus buffer, range,
listagem/glob/grep, truncamento, path/tamanho/binário, autoridade forjada e
recuperação. Confere hash inalterado, zero turnos de chat, HTML como plaintext e
203 px sem overflow. `closed` exige `workspace_unavailable`. `Close` fecha somente
a solução própria sem salvar; `Cleanup` remove somente sua pasta e manifesto.
Em execução interrompida, consultar `-Action Inspect` antes de limpar/retomar.

Resultado: build **0 erros/avisos**, **1.261 testes C# / 166 JS** verdes; relatórios
reais com **0 exceções JS**, sem API paga/probe. Identidade JSON incompatível do
pacote foi rejeitada antes de alinhar Tools a netstandard2.0. Cancelamento foi
verificado com executor determinístico/transporte fake, não com I/O longo na Exp;
troca de solução durante I/O não foi corrida. Symlink de arquivo indisponível.

Evidências: `artifacts/phase4b.binlog`, `artifacts/tests/phase4b.trx`,
`artifacts/phase4b-runtime-negative.xml`, `artifacts/read-tools-verify.json`,
`artifacts/read-tools-closed.json`, `artifacts/read-tools-webview.png`.
Install/Verify SHA-256 em `Extensions\3udphwea.yqv`; Exp/fixture próprias fechadas
e removidas, preview próprio 4176 encerrado; preexistentes 4173/4175 intocados.
Nesse checkpoint não havia edição/terminal nem invocação automática pelo modelo.

## Fatia readonly automática — antecipação da Fase 5 (2026-11-09)

Com a Exp fechada, build e testes completos:

```bash
npm run build:ui
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release --nologo -bl:artifacts/auto-tools.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=auto-tools.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
```

Aguardar ponte; regressão começa com painel fechado e chat vazio. A fixture
recusa substituir solução ou manifesto existente. O verificador possui seu
próprio servidor em porta loopback aleatória; não iniciar servidor BYOK externo.

```bash
node scripts/verify-webview.mjs
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-auto-tools-webview.mjs verify
node scripts/verify-auto-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`verify` configura apenas provedor/chave sintéticos próprios, envia pela UI com
opt-in e verifica requests assistant/tool/assistant, conteúdo real do disco
(não buffer), resultados de quatro ferramentas, HTML inerte e consentimento
resetado. Reabre histórico, testa opt-out, traversal/escrita rejeitados,
interrupção por orçamento e Cancel durante continuação. Fecha somente a solução
própria sem salvar; envio automático com solução fechada é rejeitado antes de
POST. Verifica hash do arquivo, 203 px sem overflow e exceções JS.

`cleanup` remove apenas configurações/credenciais/conversas identificadas no
manifesto, inclusive turns falhos/cancelados. Servidor/CDP fecham no finally.
Se `verify` falhar antes de fechar a solução, executar `cleanup`, depois
`context-experimental.ps1 -Action Close`, e só então limpar a fixture; não
reexecutar `Prepare` ou sobrescrever IDs de uma execução interrompida.

Resultado: clean/build **0 erros/avisos**, **1.310 C# / 179 JS** verdes; na Exp,
**12 requests loopback, 0 pagos, 0 exceções JS**. Asserção inicial que procurava
`<script>` no JSON serializado falhou pelo escape `\\u003C`; leitura do arquivo
confirmou o conteúdo e a asserção passou a verificar o campo de texto decodificado,
sem remover controles de execução/DOM. Nenhuma API paga ou probing foi usado.

Evidências: `artifacts/auto-tools.binlog`, `artifacts/tests/auto-tools.trx`,
`artifacts/auto-tools-webview.json`, `artifacts/auto-tools-webview.png` e
`artifacts/webview-verification.json`. Limitações de transporte real, I/O longo,
symlink e leitor de tela estão em `STATUS.md`. Não conclui Fases 4/5.

Em 2026-11-11, nova execução integral com o pacote local da Fase 5 chegou à
captura PNG e à asserção de exceções, com 12 requests loopback, 0 pagos e 0
exceções JS. O verificador confere assinatura e dimensões PNG e rejeita uma
cópia deliberadamente corrompida. Fixtures/credenciais próprias foram limpas,
Exp fechada e SHA verificado em `Extensions\hbngbmnp.22r`. B9 foi encerrado
quanto à regressão; a causa do timeout CDP original continua desconhecida.

## Fatia 4c — escrita manual interna (2026-11-10)

Com a Exp fechada e sem manifesto anterior:

```bash
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release -bl:artifacts/write-slice.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=write-slice.trx' --results-directory artifacts/tests
npm run check:ui
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-write-tools-webview.mjs
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
```

O verificador usa Apply/Undo reais no painel e RPCs negativos, sem provedor ou
chave. Confirma diff plaintext com finais de linha, HTML sem execução, disco
inalterado para buffer aberto não salvo, undo em lote e Undo/Redo nativos.
Mudança externa em disco, edição posterior com texto final idêntico, traversal,
ADS, caminho absoluto, modo forjado e ferramenta desconhecida são recusados.
Fecha somente a solução própria, verifica solução fechada e layout de 203 px.
Se falhar antes do Close, fechar a fixture sem salvar antes do Cleanup; não
repetir o verificador sobre escritas pendentes nem substituir o manifesto.

Falhas reais identificadas: `ToolInvocation.Arguments` atravessava identidades
JSON diferentes entre net48/netstandard; a extração ficou em Tools e retorna
strings validadas. `VsUndoHistory.UndoStack` não é implementado no VS instalado;
substituído por primitiva não mesclável e versões reiteradas. Os metadados do
VSIX têm controle negativo que rejeita a reintrodução desses acessos. O DTE
normaliza algumas leituras CRLF para LF; comparação normalizada só no verificador,
sem salvar ou reescrever o buffer. O fake concorrente de cancelamento usa fila
thread-safe sem relaxar a asserção de Cancelled. O verificador offline aguarda
`Page.loadEventFired` antes de avaliar o novo contexto: a recarga real havia
destruído a avaliação anterior. Sem retry ou polling da página.

Evidências: `artifacts/write-slice.binlog`, `artifacts/tests/write-slice.trx`,
`artifacts/write-tools-webview.json`, `artifacts/write-tools-webview.png`.
Resultado final: clean/build **0 erros/avisos**, **1.405 C# / 197 JS** verdes.
Verificador manual completo na Exp: lote disco+buffer com edição repetida,
Undo/Redo, negativos e imagem aprovados; **0 pagos / 0 exceções JS**. Baseline
local offline e readonly manual também passaram. Regressão readonly automática
ficou parcial pelo timeout da captura CDP (B9), sem repetir turnos ou aproveitar
relatório antigo como aprovação atual. Cleanup próprio e SHA finais concluídos.
Registro histórico: a 4c não habilitava escrita pelo modelo, terminal,
aprovação externa ou Agent completo. Escrita/terminal restritos vieram na 4d.

## Fatia 4d — escrita pelo modelo e terminal aprovado (2026-11-11)

Com a Exp fechada e sem manifesto anterior:

```bash
npm run build:ui
dotnet clean Lumen.sln -c Release --nologo -v:quiet
dotnet build Lumen.sln -c Release --nologo -v:minimal -bl:artifacts/agent-tools.binlog
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=agent-tools.trx' --results-directory artifacts/tests
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs verify
node scripts/verify-agent-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

O verificador cria seu servidor loopback, credencial sintética e conversas;
não chama APIs pagas. Usa a UI real para manual+dois turnos automáticos, undo de
cada lote, comandos negar/uma vez/sessão/cancelar e negação externa. Output é
lido por helper C# com cast público `DTE2`: acesso PowerShell direto a ToolWindows
no RCW retornava null, apesar de o pane Lumen existir por nome/GUID e ter texto.
Schemas negativos, HTML inerte, segredos, solução fechada/reaberta e mutação
negativa de layout fazem parte das asserções. Captura CDP é complementar.

Nesta execução, escrita/undo/terminal negar e uma vez passaram antes da falha
na inspeção COM. Após corrigir o robô, `resume` concluiu somente os cenários
restantes sem repetir escritas. Esse modo é recuperação específica desse marco,
não retomada genérica de qualquer falha: exige manifesto/configuração/conversa,
buffer original restaurado e zero escritas pendentes. Reabre o mesmo endpoint
loopback por porta já configurada; recusa host não loopback e porta ocupada.

```bash
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action OutputInspect -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs resume
```

Em outra interrupção, inspecionar o estado; não repetir `Prepare` nem `verify`
sobre o manifesto existente. Cleanup JS remove somente IDs próprios, incluindo
turnos cancelados/falhos; executar antes de fechar a Exp. Depois fechar a solução
própria sem salvar e limpar somente a pasta/manifesto da fixture. Não reiniciar
repetidamente a Exp se o robô não encontrar Lumen; verificar menu/pane atuais.
A busca alternativa de popup não teve causa comprovada e foi revertida.

Resultado: clean/build **0 erros/avisos**, **1.466 C# / 205 JS**, sem ignorados.
Retomada: **11 requests loopback, 0 pagos, 0 exceções JS**, Output/cancel/sessão/
readonly/fora/HTML/segredos/stale/reabertura/layout negativo aprovados. Relatório
explicita o marco anterior, não uma nova execução integral única. Screenshot
salvo; naquela ocasião não resolveu B9 da regressão readonly anterior, que foi
encerrado em execução posterior. Cleanup e SHA de todos os arquivos em
`Extensions\xont1ooq.0ro` confirmados, Exp fechada; sem commit/push naquele checkpoint.
Evidências: `artifacts/agent-tools.binlog`, `artifacts/tests/agent-tools.trx`,
`artifacts/agent-tools-ui-tests.log`, `artifacts/agent-tools-webview.json` e
`artifacts/agent-tools-webview.png`. Próximos itens/limites em `STATUS.md`.

A execução posterior do mesmo verificador cobriu também snapshot/patch/undo,
hash incorreto, `get_errors`, Output ativo, descoberta de shells e aprovação de
`run_build`/`run_tests` .NET. A fixture local enviou **33 requests loopback**,
sem APIs pagas ou exceções JS. Build/test usam `--no-restore` e podem executar
código e acessar rede; o prompt exibe o comando efetivo. A Exp testou negação de
ambos e execução de build autorizado, não um `run_tests` autorizado; os testes
C# exercitaram ambos com projeto inválido e exit não zero. Error List populada ou
filtrada não foi verificada. Build 0 erros/avisos; **1.493 C# / 205 JS**.
A preparação da fixture sofreu uma rejeição COM transitória e foi retomada após
inspeção, sem criar outra fixture. A Exp testou `Extensions\le2lbosl.32w`.
Após o clean/build, apenas `manifest.json` (diretório gerado aleatório) e
`catalog.json` diferiram; os demais arquivos coincidiram. O pacote reconstruído
foi reinstalado e verificado em `Extensions\n0a1c00s.vyu`, sem repetir a UI.
Cleanup foi confirmado; B9 foi encerrado em execução posterior.

## Fase 5 — validação focada dos modos (2026-11-11)

Com Exp fechada e sem fixture existente:

```bash
dotnet build Lumen.sln -c Release --nologo -v:minimal
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --nologo
npm run check:ui
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs modes
node scripts/verify-agent-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`modes` verifica somente Agent/undo, Ask/Plan rejeitando edição e comando
forjados, troca com histórico e layout negativo. Usa credencial sintética e
servidor loopback próprios. Não comprova aplicação do plano, orçamento ajustável,
compactação nem a regressão geral. Se interrompido, não apagar a fixture enquanto
houver escritas no journal da Exp: inspecionar o estado, limpar os IDs próprios
e fechar a solução/instância antes de remover a fixture. Não repetir `Prepare`
sobre manifesto existente. Resultado: **1.505 C# / 206 JS**, 6 requests locais,
0 pagos e 0 exceções JS; `artifacts/phase5-modes-webview.json`. Exp fechada,
SHA verificado em `Extensions\hbngbmnp.22r`; sem commit nesta fatia.

## Fase 5b — aplicar plano e orçamento configurável (2026-11-12)

Mesma sequência da fatia de modos, com Exp fechada, sem fixture existente e
**UI recém-aberta** (o estágio exige modo inicial Ask):

```bash
dotnet build Lumen.sln -c Release --nologo -v:minimal
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --nologo
npm run check:ui
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs plan-budget
node scripts/verify-agent-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`plan-budget` verifica: estado inicial Ask com orçamento padrão 4/8192/8 e
botão aplicar plano oculto; turno Plan com critérios de aceite; clique em
aplicar plano muda para Agent sem requests, revoga consentimentos e anuncia;
orçamento de rodadas 1 interrompe o loop com `tool_budget_exceeded` **antes de
executar qualquer tool**; budgets forjados via RPC (rodadas 99; tokens 8192.5)
rejeitados com `invalid_message` sem POST; entrada fora de faixa bloqueia o
envio localmente; orçamento customizado 2/2048/8 conclui preservando a
conversa; layout negativo a 203 px. Usa credencial sintética e servidor
loopback próprios. Não comprova compactação, retry, ParallelTools nem a
regressão geral. Mesmas regras de interrupção da fatia de modos: não apagar a
fixture com escritas no journal; não repetir `Prepare` sobre manifesto
existente. Resultado: **1.527 C# / 210 JS**, 4 requests locais, 0 pagos e
0 exceções JS; `artifacts/phase5-plan-budget-webview.json`. Exp fechada,
fixture e manifesto removidos, SHA verificado em `Extensions\vhheq1gz.wvu`.

## Fase 5c — compactação, retry e paralelismo gated (2026-11-13)

Mesma sequência da fatia 5b, com Exp fechada, sem fixture existente e
**UI recém-aberta** (o estágio exige modo inicial Ask):

```bash
dotnet build Lumen.sln -c Release --nologo -v:minimal
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --nologo
npm run check:ui
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs loop-resilience
node scripts/verify-agent-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`loop-resilience` verifica: 20 turnos Ask no modelo `fixture/small` (janela
8192) compactam o histórico com Notice `history_compacted` e replay encolhido
(primeiro POST com 2 mensagens, último entre 3 e 40); turno com 429 +
`Retry-After: 1` após uma tool executada retenta com Notice `retrying`,
3 requests e **exatamente 1** execução de tool, com id do resultado pareado
com a chamada original; modelo `fixture/parallel` (com `parallel_tool_calls`)
executa 2 leituras em paralelo e `fixture/auto` (sem a capacidade) executa em
sequência. Usa credencial sintética e servidor loopback próprios. Não comprova
a sobreposição temporal do paralelismo (provada pelos testes C# MaxActive),
o ContextBuilder integral, o AC multi-passos, a regressão geral nem o layout
203 px. Mesmas regras de interrupção das fatias anteriores: não apagar a
fixture com escritas no journal; não repetir `Prepare` sobre manifesto
existente. Resultado: **1.549 C# / 212 JS**, 27 requests locais, 0 pagos e
0 exceções JS; `artifacts/phase5-loop-resilience-webview.json`. Exp fechada,
fixture e manifesto removidos, SHA verificado em `Extensions\f3dzgvrn.jkl`.

## Fase 5d — ContextBuilder integral + AC multi-passos (2026-11-13)

Mesma sequência da fatia 5c, com Exp fechada, sem fixture existente e **UI
recém-aberta** (o estágio define o modo por envio, não exige modo inicial):

```bash
dotnet build Lumen.sln -c Release --nologo -v:minimal
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --nologo
npm run check:ui
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Open -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Prepare -InstanceId 25e762c6
node scripts/verify-agent-tools-webview.mjs agent-task
node scripts/verify-agent-tools-webview.mjs cleanup
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/open-experimental.ps1 -Action Close -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/context-experimental.ps1 -Action Cleanup -InstanceId 25e762c6
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

`agent-task` verifica: menção `@Sample.cs` captura o buffer aberto não salvo
(preferido sobre disco) e injeta `[Context: Sample.cs]` na mensagem User;
menção traversal `@../Outside.cs` é bloqueada localmente sem POST, nomeia o
token, preserva o rascunho e reabilita o envio (controle negativo); tarefa
Agent de 5 rodadas com orçamento 8/32768/16 — create `Calculator.cs` + create
`CalculatorTests.cs` + create `Calculator.csproj` dedicado + `run_build`
aprovado (exit 0; o teste referencia `Calculator.Add`, então o build prova que
ambos os fontes compilaram juntos) + resposta; undo em lote restaura os 3
arquivos; colisão mid-task `file_exists` recupera sem duplicar escrita. Usa
credencial sintética e servidor loopback próprios. **O orçamento padrão 4
aborta a rodada do build** — o estágio eleva para 8 rodadas de propósito.
Não comprova skills ativas (Fase 6), a regressão geral nem o layout 203 px.
Mesmas regras de interrupção das fatias anteriores: não apagar a fixture com
escritas no journal; não repetir `Prepare` sobre manifesto existente.

**Achado que moldou o estágio:** o `.csproj` legado da fixture vive em um
buffer RDT somente-leitura do project system do VS cujo snapshot muda
continuamente; `edit_file` recusa com `edit_conflict` (comportamento correto
do produto). Por isso o modelo cria um projeto dedicado em vez de editar o
csproj aberto. Curingas `*.cs`/SDK-style não servem: puxariam o
`AcceptanceTests.cs` do estágio de aceitação e quebrariam o
`BuildFixtureError`. Se o estágio falhar com 4 eventos Completed em vez de 5,
inspecionar os cards de tool via CDP antes de suspeitar de corrida de eventos.

Resultado: **1.550 C# / 217 JS**, 8 requests locais, 0 pagos e 0 exceções JS;
`artifacts/phase5-agent-task-webview.json`. Exp fechada, fixture e manifesto
removidos, SHA verificado em `Extensions\4hgcddl3.sps`.
