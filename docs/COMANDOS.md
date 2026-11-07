# COMANDOS — Fase 0

Testados em **2026-11-07**, na raiz `C:\Users\Hanjel\Documents\Extensão`.
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

Esse comando conecta ao WebView2 real, não a um mock. Verifica:

- Handshake, eco multilinha, markdown/highlight, duas colunas de diff.
- 500 mensagens com DOM limitado e navegação à última mensagem.
- Composer expansível; mensagem inválida e surrogate isolado rejeitados pelo C#;
  ping posterior ainda responde.
- Reload com cache desabilitado e offline via CDP, sem desligar a rede da máquina.
- Todos os requests de UI permanecem em `https://lumen.local`.
- Layouts emulados 203×422, 320×320, 375×700, 900×900 e overflow forçado detectado.

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

O estado final deixado nesta máquina: hive `18.0_25e762c6Exp` resetada e com
Lumen **instalado e verificado** em `Extensions\nrqagayt.p4k`; nenhuma instância
Exp aberta; perfil principal do VS intocado.
