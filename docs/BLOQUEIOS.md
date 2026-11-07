# BLOQUEIOS — Lumen

Bloqueios, pré-requisitos e armadilhas conhecidas. Ao resolver um item, mova-o
para "Resolvidos" com a evidência. Ao encontrar um novo, registre com comando,
saída e `arquivo:linha` — **não invente solução**.

---

## Abertos

### B1 — Workload de desenvolvimento de extensão: **RESOLVIDO**

**Estado:** instalado com sucesso.

O usuário autorizou instalar
`Microsoft.VisualStudio.Workload.VisualStudioExtension` na instância Insiders.

**Verificação:**
```bash
vswhere -prerelease -all \
  -requires Microsoft.VisualStudio.Workload.VisualStudioExtension \
  -property installationPath
# → C:\Program Files\Microsoft Visual Studio\18\Insiders
```

**Log do installer:** `%TEMP%\dd_installer_20261106215357.log` —
`Exit Code: 0` (sucesso). Houve warnings de rede ao tentar atualizar os feeds
(`aka.ms/vs/channels`, `aka.ms/vs/installer/latest/feed`) — **não fatais**.

**Componentes instalados:**
- `Microsoft.VisualStudio.Workload.VisualStudioExtension`
- `Microsoft.VisualStudio.Component.VSSDK`

**Nota:** o script PowerShell reportou `SETUP_EXIT=-2146233029` (0x8013153B),
mas o installer em si retornou **0** e o vswhere confirma a instalação.
Isso foi um artefato de COM interop com o processo elevado.

### B2 — Disco com pouco espaço

`C:` tinha **19 GB livres** (93% usado) antes da instalação do workload; caiu
para **17 GB** durante o download. Builds de VSIX, cache NuGet e a hive
experimental consomem espaço.

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

### B5 — Versões de pacotes NuGet não fixadas

As versões em `ARQUITETURA.md` §2.2 são as mais recentes vistas na pesquisa, mas
**não foram testadas juntas**. `Microsoft.VSSDK.BuildTools` 18.9.820 e
`Microsoft.VisualStudio.SDK` 17.14.40265 podem não casar.

**Mitigação:** primeira tarefa da Fase 0. Fallback comprovado em produção:
`Microsoft.VSSDK.BuildTools` **17.14.2120** (usado pelo vs-agentic).

---

## Resolvidos

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
**Pendente:** repetir para `net48` com
`Microsoft.NETFramework.ReferenceAssemblies` (tarefa da Fase 0).

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

## Armadilhas que NÃO são bloqueios (mas vão morder)

- **VSIX mesma Id+Version é silenciosamente ignorado.** Desinstale antes ou
  incremente a versão. O deploy precisa provar que atualizou.
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