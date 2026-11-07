# BLOQUEIOS — Lumen

Bloqueios, pré-requisitos e armadilhas conhecidas. Ao resolver um item, mova-o
para "Resolvidos" com a evidência. Ao encontrar um novo, registre com comando,
saída e `arquivo:linha` — **não invente solução**.

---

## Abertos

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

---

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