# ARQUITETURA — Lumen

Desenho técnico de 2026-11-06, atualizado com evidências da Fase 0 em **2026-11-07**.
Fatos locais (inspeção da máquina) têm a confiança mais alta. Fatos de
documentação/pesquisa estão com URL. O que falta confirmar está `A CONFIRMAR`.

---

## 1. Ambiente verificado localmente

| Fato | Evidência |
| --- | --- |
| VS: **Community 2026 Insiders 18.12.12328.396**, canal `VisualStudio.18.Preview`, instanceId **`25e762c6`** | `vswhere -prerelease -all -format json` |
| Caminho: `C:\Program Files\Microsoft Visual Studio\18\Insiders` | idem |
| Expira em **2027-01-26** | `expirationDate` no vswhere |
| `devenv.exe` roda em **.NET Framework 4.7.2** — **não** em .NET 10 | `Common7/IDE/devenv.exe.config` → `<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2" />`; não existe `devenv.runtimeconfig.json` nem `coreclr.dll` no IDE |
| VS traz hosts out-of-process **.NET 8.0.33** e **10.0.14** (com WindowsDesktop e AspNetCore) | `dotnet/net{8,10}.0/runtime/shared/` |
| SDK de extensibilidade moderno presente, **18.12.20** | `CommonExtensions/Microsoft/Extensibility/Legacy/Extensibility/18.12.20/` |
| **MSBuild:** `…\18\Insiders\MSBuild\Current\Bin\MSBuild.exe` | existe (não fica sob `Common7/IDE`) |
| **VSIXInstaller:** `…\18\Insiders\Common7\IDE\VSIXInstaller.exe` | existe |
| **WebView2 Runtime 156.0.4314.58** | `C:\Program Files (x86)\Microsoft\EdgeWebView\Application` |
| SDK .NET local: **11.0.100-rc.3** (único) | `dotnet --list-sdks` |
| **SDK 11 RC compila `net8.0-windows` + WPF: 0 erros, 0 avisos, 8,77 s** | probe real em temp, removida depois |
| Git 2.55.0.windows.3; identidade `Hangel <gustavorangel8080x@gmail.com>` | `git --version`, `git config --global` |
| **`gh` CLI ausente** | `command not found` → remote GitHub é ação manual do usuário |
| Workload `Microsoft.VisualStudio.Workload.VisualStudioExtension` **INSTALADO** | `vswhere -prerelease -all -requires …` → `C:\Program Files\Microsoft Visual Studio\18\Insiders`; componentes: `VSSDK` e `VisualStudioExtension` |
| Pacotes 18.x do SDK moderno **não estão no nuget.org** nem no fallback local | fallback `…\Shared\NuGetPackages` só tem `microsoft.visualstudio.javascript.sdk`; nenhum `.nupkg` de Extensibility no install |
| Hive experimental criada em **2026-11-07** | `%LocalAppData%\Microsoft\VisualStudio\18.0_25e762c6Exp`; resetada com `CreateExpInstance /Reset /VSInstance=18.0_25e762c6` e reinstalada, com SHA-256 conferido |
| Disco `C:` com **19 GB livres** (93% usado) | `df -h /c` |
| `devenv.exe` **não estava rodando** no momento da instalação do workload | `tasklist` |

> **Workload instalado**, confirmado pelo `vswhere`. Não é pré-requisito do
> caminho escolhido (VSSDK clássico restaura do nuget.org), mas fornece tooling
> de VSIX e templates. O antigo comando com `--wait` falhou; esse switch não
> existe no installer local. Evidência em `BLOQUEIOS.md` R1 e B1.

---

## 2. Decisão de arquitetura — VSSDK clássico, `net48`, WPF + WebView2

**Escolhida pelo usuário em 2026-11-06.** Não reabrir sem motivo novo.

### 2.1 Por que este caminho

| Critério | VSSDK net48 + WebView2 | Out-of-proc + Remote UI |
| --- | --- | --- |
| Pré-requisito de toolchain | **nenhum** — pacotes no nuget.org | workload + pacotes 18.x fora do nuget.org |
| Liberdade de UI | **total** (HTML/CSS/JS, code-behind) | MVVM-only, sem code-behind, sem controles de terceiros |
| Markdown / highlight / diff | bibliotecas JS prontas | implementar à mão |
| Temas | CSS variables | estilos WPF restritos |
| Risco de travar a execução autônoma | **baixo** | alto |
| Isolamento da UI pesada | WebView2 já renderiza em processo separado | processo separado |
| Direção da Microsoft | legado, **mas suportado no 18.x** | recomendada |

Fatos que sustentam a decisão:

- **VSSDK é totalmente suportado no VS 18.x.** Extensões `net472` do VS 2022
  "simplesmente funcionam" no 2026; só o **lower bound** do manifesto importa.
  Fonte: <https://devblogs.microsoft.com/visualstudio/modernizing-visual-studio-extension-compatibility-effortless-migration-for-extension-developers-and-users/>
- Existe extensão real publicada construindo VSIX `net472` via `dotnet build`
  com `Microsoft.VSSDK.BuildTools`: <https://github.com/adospace/vs-agentic/blob/main/CONTRIBUTING.md>
- O shell do VS 2026 **continua em .NET Framework** — a afirmação "VS migrou para
  .NET" vale para componentes out-of-process, não para o `devenv.exe`.
  Fonte: <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/how-it-works/models/extensibility-models?view=visualstudio>
- **Remote UI** é MVVM-only: DataContext precisa ser `[DataContract]`/`[DataMember]`,
  comandos são `IAsyncCommand` (não `ICommand`), **sem code-behind, sem controles
  custom/terceiros, sem merged resource dictionaries**. WebView2 é possível, mas
  obter referência do controle para interop JS a partir de um `RemoteUserControl`
  ficou **sem solução confirmada**.
  Fontes: <https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio-extensibility/how-it-works/remote-ui?view=visualstudio>
  · <https://learn.microsoft.com/en-us/answers/questions/2149139/how-to-add-webview2-to-a-visualstudio-extensibilit>

### 2.2 Pacotes e TFM

| Projeto | TFM | Pacotes |
| --- | --- | --- |
| `Lumen.Core` | `netstandard2.0` | `System.Text.Json` **10.0.14** na Fase 0 |
| `Lumen.Providers` | `netstandard2.0` | `System.Text.Json` |
| `Lumen.Agent` | `netstandard2.0` | — |
| `Lumen.Tools` | `netstandard2.0` | domínio Core; guard Windows, leitura e aprovação; adaptadores VS no host |
| `Lumen.Vsix` | **`net48`** | `Microsoft.VisualStudio.SDK` **17.14.40265**, `Microsoft.VSSDK.BuildTools` **18.5.38461**, `Microsoft.Web.WebView2` **1.0.4314.35**, `Microsoft.NETFramework.ReferenceAssemblies` **1.0.3** |
| `Lumen.Tests` | `net10.0` | xUnit **2.9.3**; referencia `Lumen.Core`, sem assemblies do VS |

**Confirmado em 2026-11-07:** a combinação acima restaura e gera VSIX via
`dotnet build -c Release` no SDK **11.0.100-rc.3.26522.148**, com zero erros e
zero avisos. Probe WPF `net48` + ReferenceAssemblies 1.0.3: 49,12 s, zero erros/
avisos. Não foi necessário instalar SDK 10. `BuildTools` 18.9.820 está **unlisted**;
foi usada 18.5.38461, do template SDK-style oficial, em vez de adotá-la.
`Lumen.Core` usa System.Text.Json **10.0.14**; testes: Microsoft.NET.Test.Sdk
**18.11.0**, xUnit **2.9.3**, runner Visual Studio **4.0.1**. Fontes e detalhes
reproduzíveis em `PESQUISA.md` §2.7 e `COMANDOS.md`.

**Atualização 4b (2026-11-09):** `Lumen.Tools` usa netstandard2.0, sem refs do VS,
mas seu guard depende de Windows/Win32. Na Exp, Tools net48 referenciava JSON
10.0.0.14 enquanto Core/Providers referenciavam 10.0.0.0 (mesmo pacote 10.0.14),
causando `MissingMethodException` ao acessar `ToolInvocation.Arguments`. O teste
`CoreProvidersAndToolsShareJsonTypeIdentity` rejeitou o pacote real incompatível;
alinhar o TFM corrigiu o runtime, sem redirects. O host net48 recebe argumentos
por JSON/string e mantém os adaptadores VS. Build e execução na Exp confirmados.

O VSSDK exclui algumas DLLs de nomes `System.*` por padrão; o target
`IncludeRuntimeDependencies` empacota as dependências CopyLocal de runtime.
O teste de pacote falhou com `System.Text.Json.dll` ausente antes da correção.
Nenhuma assembly do SDK do VS é distribuída (`ExcludeAssets="runtime"`).

**Regra:** `Core`, `Providers` e `Agent` **não referenciam nenhuma assembly do
Visual Studio**. É isso que permite testá-los fora do VS, em .NET 10, com
`dotnet test` rápido.

### 2.3 UI: WebView2 dentro de um `ToolWindowPane`

```
Lumen.Vsix (net48, in-process)
  LumenToolWindow : ToolWindowPane
    └ WPF UserControl (code-behind permitido)
        └ Microsoft.Web.WebView2.Wpf.WebView2
            └ wwwroot/index.html  (chat em HTML/CSS/JS)
                 markdown-it + highlight.js + diff2html
```

**Ponte C# ↔ JS:** `CoreWebView2.WebMessageReceived` ← `window.chrome.webview.postMessage`
e `CoreWebView2.PostWebMessageAsJson` → JS. Protocolo de mensagens tipado,
versionado, com ids de correlação para pedidos/respostas.

**Gotcha conhecido e obrigatório:** definir
`CoreWebView2CreationProperties.UserDataFolder` explicitamente (ex.:
`%LocalAppData%\Lumen\WebView2`). Sem isso o controle pode renderizar **em branco**.
Esse foi o fix confirmado na discussão com a Microsoft.

**Assets JS:** empacotados no VSIX como `Content`/`Resource`, servidos via
`SetVirtualHostNameToFolderMapping` (ex.: `https://lumen.local/`) — **não**
carregar de CDN. Nenhuma dependencia de rede para a UI.

**Sanitização:** markdown-it com `html:false`, `linkify` com allowlist de
esquemas (`http`, `https`, `mailto`). **Nunca** habilitar HTML cru vindo do
modelo. CSP restritiva no `index.html`.

### 2.4 Threading — o risco real do in-process

Como o pacote é in-process, **todo** trabalho bloqueante sai da UI thread:

- HTTP, streaming, parsing, loop de agente: `async`/`await` + `Task.Run`.
- Acesso a serviços do VS: `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync`.
- Atualizar a UI: `PostWebMessageAsJson` **na thread UI/STA criadora** via JTF ou
  dispatcher do WPF. A API **não é thread-safe**. `WebMessageReceived` também
  chega nessa thread; mover trabalho pesado explicitamente para background.
  Correção verificada em 2026-11-07:
  <https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model>.
- **Nunca** `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` em código que
  possa rodar na UI thread — deadlock clássico do VS.
- Analyzer de threading nos testes de revisão; `Microsoft.VisualStudio.Threading`
  já vem com `JoinableTaskFactory` e analyzers.

O renderer do WebView2 é um processo separado, então a renderização pesada do
chat **não** compete com a UI thread do VS.

### 2.5 Integração com o VS (in-process, APIs maduras)

| Necessidade | API |
| --- | --- |
| Tool window | `ToolWindowPane` + `ProvideToolWindowAttribute` |
| Editar documento aberto com undo | MEF `ITextBuffer.CreateEdit()` / `ITextEdit.Apply()`; `ITextUndoHistoryRegistry.TryGetHistory`, `ITextUndoHistory.CreateTransaction` / `Undo` |
| Documento ativo e seleção | `IVsTextManager.GetActiveView`, MEF `IWpfTextView.Selection`, `ITextSnapshot` |
| Solução e projetos | `IVsSolution`, `IVsHierarchy`, `DTE.Solution` |
| Error List | `ErrorListProvider` / `IVsErrorList` |
| Output do shell | `IVsOutputWindowPane` + pane próprio "Lumen" |
| Settings | `DialogPage` (Tools→Options) + página própria |
| Comandos e atalhos | `.vsct` + `OleMenuCommandService` |
| Tema do VS | `VsColors` / `VsResourceKeys`, evento `VSColorTheme.ThemeChanged` → republica CSS variables |
| Status bar | `IVsStatusbar` |

**Contexto explícito (3c):** `VisualStudioContextSource` captura arquivo aberto
(incluindo buffer não salvo), seleção, inventário da solução ou Output ativo.
`ByokSession` guarda até oito `ContextSnapshot` imutáveis (65.536 caracteres cada,
131.072 no total). A UI recebe prévia em texto puro e envia apenas IDs no chat;
validação da solução/path ocorre antes de credenciais e HTTP. Contexto enviado
é um `TextPart` separado do prompt, persistido no histórico sem recaptura.
Falha/cancelamento retém IDs para retry; resultado de chat os consome. IDs
liberados, consumidos ou de outra sessão são obsoletos. Troca/fechamento da
solução impede enviar snapshots ainda pendentes. Não há coleta automática.

Disco é lido em background com encoding estrito e handle validado; documentos
abertos não caem silenciosamente para disco. Inventário é limitado a 256
arquivos e 1.024 nós/nomes, sem leitura de conteúdo em massa. O histórico local
continua JSON não criptografado: contexto sensível deve ser removido antes de
Enviar. Limites por caracteres não substituem orçamento de tokens (Fase 5).

**Terminal: não existe API pública** em nenhum dos dois modelos. O
`Microsoft.VisualStudio.Terminal.dll` local é um serviço pty interno do
ServiceHub, **não verificado** para uso por extensão.
→ **Usar `System.Diagnostics.Process`** + pane de output próprio. Isso é
suficiente: captura stdout/stderr/exit code, timeout, cancelamento, e roda
cmd / powershell / pwsh / bash com caminho explícito.

### 2.6 Camadas

```
Lumen.Core        netstandard2.0   domínio puro: Conversation, Message,
                                 ContentPart, ModelInfo, ModelCapabilities,
                                 ReasoningIntent, StreamEvent, ToolSpec,
                                 Skill, Theme, ChatMode
        ^
Lumen.Providers   netstandard2.0   adapters BYOK, descoberta, CapabilityResolver,
                                 streaming normalizado, ICredentialStore
        ^
Lumen.Agent       netstandard2.0   AgentLoop, ContextBuilder, modos,
                                 orçamento, compactação de histórico
        ^
Lumen.Tools       netstandard2.0   WorkspaceGuard Windows, file tools,
                                 ApprovalBroker, EditJournal; backend shell
        ^
Lumen.Vsix        net48            ToolWindowPane, WebView2 host, ponte JS,
                                 adaptadores VS de solução/editor, settings,
                                 comandos, temas, DI composition root
Lumen.Tests       net10.0          xUnit; mocks; fixtures SSE; controles negativos
wwwroot/          (em Lumen.Vsix)  chat HTML/CSS/JS, markdown-it, highlight.js,
                                 diff2html — tudo local, sem CDN
```

### Escrita manual interna

`byok.tool.write` recebe apenas nome (`create_file`/`edit_file`), caminho relativo
(até 1.024 caracteres) e conteúdo completo (até 65.536 caracteres). O host captura
a solução antes da execução; nenhum settings, segredo ou provedor é acessado.
O painel não encaminha conteúdo ao modelo, não declara modo Agent completo e
não oferece shell ou aprovação externa. `WriteToolCatalog.Validate` extrai duas
strings dentro de netstandard; o adaptador net48 não acessa `ToolInvocation.Arguments`.
O mesmo pacote JSON 10.0.14 referencia assembly 10.0.0.0 em netstandard e 10.0.0.14
em net48; passar/acessar `JsonElement` nessa fronteira causou MissingMethod na Exp. `byok.tool.write.status` consulta somente
o contador; undo é uma ação explícita por `byok.tool.write.undo`.

Arquivos fechados usam gravação atômica e `EditJournal` com bytes anteriores e
posteriores, guard de origem, limites e preflight de conflitos. Arquivos abertos
usam `IVsRunningDocumentTable` + `IVsEditorAdaptersFactoryService.GetDocumentBuffer`,
edição MEF na UI thread e transação `ITextUndoHistory`; não são salvos/recarregados.
Undo exige conteúdo/versão reiterada esperados e marcador `ITextUndoPrimitive`
aplicado; transações externas, fechamento de buffer ou mudança de solução são
recusados. O marcador não mescla e acompanha Undo/Redo nativos. Pilhas e
`LastUndoTransaction` não são consultados: a implementação instalada lança
`NotSupportedException`, e `CanUndo` retorna sempre true. Cada undo confirma o
marcador e o snapshot restaurado antes de remover o registro; os textos e
referências auxiliares são liberados mesmo se o marcador persistir no redo nativo.
Disco é preparado em background; o commit e a revalidação de solução/arquivo
fechado ocorrem sem yield na UI thread, inclusive no undo. `BeginTurnAsync`
separa cada turno automático de lotes de escritas manuais consecutivas; undo
reverte apenas o último lote não vazio, preservando anteriores. Fronteiras de
journal/buffers permitem retry de undo parcial. Só em memória nesta sessão.
Até 64 alterações;
texto retido de buffer até 1 Mi caracteres, journal de disco até 16 MiB.

Diff limitado é texto puro, com +/−, finais CRLF/LF/CR/none e truncamento explícito.
Cancelamento anterior ao commit não altera o alvo; sucesso já aplicado não é
ocultado por cancelamento tardio. Undo parcial remove apenas registros concluídos.

### 2.7 Fluxo de uma mensagem (modo Agent)

**Fatias automáticas readonly/4d:** `ByokSession` usa `ToolChatLoop` em
`Lumen.Providers`, sem introduzir os modos completos da Fase 5.
`byok.chat.readTools`, `writeTools` e `terminal` são booleanos opcionais;
write/terminal implicam leitura e modo Agent restrito às ferramentas consentidas.
Ausentes/false não capturam workspace nem anunciam/executam ferramentas.
Opt-in vale somente para aquele envio; a UI avisa sobre conteúdo enviado,
custo e histórico local não criptografado. Formulários manuais continuam
separados do chat.

Antes de ler credenciais ou fazer POST, o host captura raiz e caminho da solução
via `IReadToolExecutor.CaptureAsync`. `VisualStudioReadTools` valida essa mesma
solução antes/depois de cada I/O; leitura automática não lê buffers não salvos.
`read_file`, `list_dir`, `find_files` e `grep` são anunciadas com leitura;
`create_file`/`edit_file` somente com escrita; `run_command` somente com terminal.
Writes anunciam path de 1.024 e conteúdo de 65.536 caracteres. Caminho externo
absoluto de drive é validado pelo guard e exige aprovação por operação; UNC,
traversal e ADS continuam rejeitados. Execução sequencial só ocorre
após argumentos finalizados e `Done(ToolUse)`. Ferramentas desconhecidas ou
argumentos rejeitados viram resultados correlacionados de erro, sem executor.
Conteúdo de arquivo é dado não confiável, não instrução. UI mantém resultados
em texto puro; falhas de preflight não concedem autoridade de execução.

Limites fixos por turno: quatro POSTs, oito invocações, 8.192 tokens de saída
solicitados cumulativamente, deadline de dois minutos, stream de 8 Mi caracteres
ou 32.768 eventos e resultados de ferramentas de 131.072 caracteres no total.
Input tem teto de 262.144 caracteres/2.048 mensagens e reserva conservadora de
contexto por modelo; não há compactação silenciosa, retry ou reexecução.

Histórico mantém um par usuário/assistente por turno, com calls, resultados e
resposta em partes ordenadas. Replay expande essas partes em mensagens separadas
assistant/tool/assistant e preserva `ProviderState` assinado. Turns falhos,
cancelados e pares legados com chamadas órfãs não são reenviados. Eventos
`ToolExecution` exibem Running/Completed/Failed/Cancelled; cancelamento mantém
o gate até a resposta nativa. Credenciais são filtradas antes de UI, replay e
arquivo, inclusive quando divididas em deltas ou escapadas em JSON.

Na 4d, comandos usam lista escapada, resolução do executável e processo Windows
criado suspenso, associado a Job Object antes de resume. Timeout/cancelamento e
saída do pai encerram descendentes; drenagem dos pipes tem limite de dois segundos.
Trabalho de processo ocorre em background; o pane `Lumen` do Output recebe o
resultado. Solução é revalidada após aprovação antes de iniciar o processo;
eventos públicos de abertura/fechamento revogam o consentimento de sessão.
Aprovação é correlacionada ao chat ativo e sempre começa em Negar.
Antes da escrita o loop reserva capacidade para o maior resultado; escrita
concluída é arquivada/exibida antes de honrar cancelamento tardio.

O fluxo completo abaixo é planejado; diagnósticos, detecção completa de shells,
`apply_diff` e modos completos continuam pendentes.

```
usuário digita no WebView2
  -> postMessage -> C# (callback na UI thread; trabalho pesado delegado ao background)
  -> ContextBuilder (seleção, @arquivos, skills ativas, system prompt do modo)
  -> IChatProvider.StreamAsync(model, messages, tools, reasoningIntent)
  -> normaliza para StreamEvent
  -> PostWebMessageAsJson -> UI renderiza incremental
  -> ToolCallEnd => ApprovalBroker.Decide(tool, args)
        leitura             -> allow
        edit dentro da sol. -> allow + diff + registra undo
        fora do workspace   -> prompt na UI
        run_command         -> prompt + "sempre permitir nesta sessão"
  -> ToolExecutor executa (Process / editor buffer / System.IO)
  -> resultado vira tool message -> repete até concluir ou estourar orçamento
```

Cancelamento: `CancellationToken` ponta a ponta. Parar o stream não deixa
ferramenta órfã, processo zumbi nem edição pela metade.

### 2.8 Modos

| Modo | Ferramentas | Escrita | System prompt |
| --- | --- | --- | --- |
| **Ask** | somente leitura | nunca | explicar, citar `arquivo:linha` |
| **Plan** | somente leitura | nunca | plano em fases com critérios de aceite; não implementar |
| **Agent** | leitura + escrita + shell | conforme `AGENTS.md` §1 | executar até concluir, verificando com build/teste |

A matriz é aplicada no **ApprovalBroker**, não só na UI. Esconder botão não é
segurança.

### 2.9 Descoberta de modelos e capacidades

Implementa a estratégia em camadas de `PESQUISA.md` §1.6:
metadado nativo → registry curado → heurística marcada → probe **opt-in**.

```csharp
[Flags] enum ModelCapabilities {
    None = 0, Text = 1, Vision = 2, Tools = 4, ParallelTools = 8,
    Reasoning = 16, Streaming = 32, StructuredOutput = 64, Pdf = 128
}
```

Mais `ReasoningSupport { Kind, Effort[], Mandatory }`, `ContextWindow`,
`MaxOutputTokens`, `CapabilitySource { Native, Registry, Heuristic, Probe,
UserOverride }` e `Confidence`. **A UI mostra a origem** — o usuário precisa
saber quando um badge é chute.

Isto é mais rico que o BYOK nativo do VS, cujo `CopilotModelCapabilities` só tem
`None, ToolCalls, ParallelToolCalls, Vision` — **sem flag de raciocínio**.

### 2.10 Skills — compatível com o formato nativo

**Descoberta importante:** o VS 2026 tem **Agent Skills nativo desde 18.5**,
usando `SKILL.md` no padrão **agentskills.io**, lido de:
`.github/skills/`, `.claude/skills/`, `.agents/skills/`, `~/.copilot/skills/`.
Fonte: <https://learn.microsoft.com/en-us/visualstudio/ide/copilot-agent-skills?view=visualstudio>

**Decisão:** adotar **o mesmo formato e os mesmos diretórios**, mais
`.lumen/skills/`. Assim as skills que o usuário já tem funcionam no Lumen sem
conversão, e skills do Lumen funcionam no Copilot nativo.

- Frontmatter YAML: `name`, `description` (+ campos opcionais).
- **Progressive disclosure:** só `name` + `description` no system prompt; o
  corpo entra só quando a skill é acionada. Controla custo de token.
- Precedência: projeto > usuário; dentro do projeto, `.lumen/` > `.github/` >
  `.claude/` > `.agents/`.
- Acionamento: automático por descrição, explícito `/nome`, manual pela UI.

### 2.11 Personalização visual

- Seguir o tema do VS por padrão; reagir a `VSColorTheme.ThemeChanged`.
- Tema = conjunto de **CSS variables** injetadas no WebView2. Trocar tema é
  trocar um objeto, sem reload.
- JSON de tema: cores, fonte do chat, fonte de código, tamanho, densidade,
  raio de borda, cor de destaque por modo.
- Preview ao vivo, reset, importar/exportar. Persistência por usuário.

### 2.12 Segurança

- **WorkspaceGuard:** `Path.GetFullPath` + verificação de prefixo contra raízes
  permitidas antes de qualquer IO. Rejeita traversal, symlink para fora, UNC
  não autorizado.
- **Shell:** nunca concatenação. `ProcessStartInfo.ArgumentList` (ou escaping
  correto no net48) + caminho do executável explícito. Blocklist como defesa em
  profundidade — **não** substitui confirmação.
- **Diff + undo em lote:** toda edição automática registra o estado anterior;
  um comando reverte o turno inteiro.
- **Chaves:** Windows Credential Manager, fallback DPAPI (`PESQUISA.md` §1.10).
  Nunca em log, settings, ou pacote.
- **Markdown:** `html:false`, allowlist de esquemas de link, CSP restritiva.
  Nada de executar conteúdo vindo do modelo.
- **Ponte JS:** validar toda mensagem recebida; ids de correlação; nunca
  `eval` de string vinda do modelo.

---

## 3. O que existe nativo no VS 18 (referência, não dependência)

Inspecionado em
`CommonExtensions/Microsoft/Microsoft.VisualStudio.Copilot.Contracts/`
(`Microsoft.VisualStudio.Copilot.xml`, versão 18.12.1566.14353):

- **BYOK nativo** desde 18.10 (preview, ligado por padrão, inclusive Community):
  Microsoft Foundry, OpenAI, Anthropic, Ollama + endpoints custom.
  Tipos: `CopilotBYOKModel`,
  `BringYourOwnKey.{CustomCopilotBYOKModel, KnownModel, Model, ModelProvider,
  ModelSettings, WireApiType, ProviderConfigurationId}`.
  `KnownModel` expõe `IsToolCallingEnabled`, `IsVisionEnabled`,
  `MaxInputTokens`, `MaxOutputTokens`, `WireApi`.
- `CopilotModelCapabilities`: **só** `None, ToolCalls, ParallelToolCalls, Vision`.
  **Sem raciocínio** → diferencial do Lumen.
- Tools nativas: `KnownCopilotFunctions.ToolNames` = `ReadFile, FindFiles,
  GetErrors, GetFilesInProject, GetProjectsInSolution, RemoveFile, CreateFile,
  RunBuild, DeployProject, GetTests, RunTests, RunCommandInTerminal,
  GetBackgroundTerminalOutput, GetOutputWindowLogs, EditFiles, Plan,
  AskQuestion, Handoff`.
- **MCP nativo** (17.14+/2026): consumido via `.mcp.json` de solução/usuário,
  com registries e OAuth.
  Fonte: <https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers?view=visualstudio>
- **API de extensão do Copilot emergente e NÃO documentada**, verificada por
  inspeção de binário em `Microsoft.VisualStudio.Extensibility.Copilot.dll`:
  `CopilotExtensibility`, `ICopilotAgentSkillsProvider`,
  `ICopilotMarkdownAgentProvider`, `ICopilotSdkPluginProvider`,
  `ICopilotContextProvider`, `ICopilotResponder`,
  `CopilotAgentProviderConfiguration`.
  → **PROIBIDO usar** (`AGENTS.md` §7). Sem doc pública, sem contrato.
- Parte da API é `Internal.*` / `UnstableInternalApi.*` → igualmente proibido.

**Nenhuma dependência em `Microsoft.VisualStudio.Copilot*`.** O Lumen pode ser
**cliente MCP** por conta própria (fase futura, fora do escopo atual).

---

## 4. Build, debug e install

Comandos completos e resultados em `COMANDOS.md`. Ciclo testado em 2026-11-07:

```bash
npm run build:ui
dotnet build Lumen.sln -c Release
dotnet test tests/Lumen.Tests/Lumen.Tests.csproj -c Release --no-build
powershell.exe -NoProfile -File scripts/deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
```

O script exige a Exp fechada, desinstala somente Lumen, instala, executa
`devenv /RootSuffix Exp /UpdateConfiguration` e compara SHA-256 com o VSIX.
**A atualização do cache foi necessária:** antes dela, o VSIX estava instalado
mas o menu não aparecia. Depois, Exibir → Lumen abriu e a ponte respondeu.
Nunca usa `/a`, nunca modifica o perfil principal, nunca reseta uma hive.

**Gotchas verificados localmente:**

- Mesma Id+Version **atualizou/reparou** no instalador 18.12, contrariando a
  hipótese anterior. O script mantém uninstall-first e verificação de conteúdo.
- `CreateExpInstance /Reset /VSInstance=18.0 /RootSuffix=Exp` reseta `18.0Exp`,
  **não** a hive real `18.0_25e762c6Exp` — e ainda retorna exit 0. O parâmetro é o
  prefixo da hive: `/VSInstance=18.0_25e762c6` é o comando válido, testado em
  2026-11-07 (25 entradas/156 arquivos → 1/6, zero Lumen). Ver `COMANDOS.md`.
  O binário fica em `VSSDK\VisualStudioIntegration\Tools\Bin`, não em `Common7\IDE`.
- `/logFile` recebe nome de arquivo e grava em `%TEMP%`; `/instanceIds` é plural.
- Os testes verificam o manifesto efetivamente empacotado, dependências e assets;
  não basta conferir os fontes. `dotnet clean` + build também foi validado.
- F5 está configurado (`VsixDeployOnDebug=true`), mas o ciclo exercitado foi CLI
  e abertura da janela pelo menu, não depuração F5.

**Manifesto VSIX clássico para VS 2026:** o VS 2026 **ignora upper bounds**.
Use `Version="[18.0,)"` para 2026-only, ou `[17.0,)` para incluir 2022.
`ProductArchitecture` = `amd64` (e `arm64` se for suportar).

> Diferença importante: extensões **pure out-of-process não têm
> `extension.vsixmanifest`** (metadados vão em código → `extension.json`).
> O Lumen é **clássico/híbrido**, então **tem** `extension.vsixmanifest` normal.
> O molde com `ExtensionType="VisualStudio.Extensibility"` e
> `<DotnetTargetVersions>net10.0</DotnetTargetVersions>` observado em
> `AppModernizationForDotNet` **não se aplica** ao Lumen — aquela extensão é
> out-of-process com container in-proc.

---

## 5. Riscos e mitigação

| Risco | Impacto | Mitigação |
| --- | --- | --- |
| Deadlock na UI thread (in-process) | VS congela | `AGENTS.md` + §2.4: nunca `.Result`/`.Wait()`; JTF; analyzers; teste de carga na Fase 8 |
| WebView2 renderiza em branco | UI não aparece | `UserDataFolder` explícito (§2.3); spike na Fase 0 |
| Versões de VSSDK.BuildTools / SDK não casarem | build falha | Fase 0 valida restore+build antes de qualquer feature; versão comprovada do vs-agentic como fallback |
| Assets JS não carregarem no VSIX | chat quebrado | `SetVirtualHostNameToFolderMapping`; teste abre a UI e asserta render; sem CDN |
| Ponte JS↔C# com mensagens malformadas | crash / injeção | schema validation + correlação ids + CN na Fase 3 |
| Só .NET 11 RC SDK instalado | build net48 falha | `Microsoft.NETFramework.ReferenceAssemblies` no VSIX; probe real na Fase 0 |
| Disco com 19 GB livres | workload/build falha por espaço | conferir antes de build grande; limpar `bin/obj` e cache NuGet se necessário |
| Insiders expira 2027-01-26 | ambiente de teste morre | range `[18.0,)`; testar em 18 estável quando houver |
| Linha de modelos muda rápido | detecção errada | registry atualizável sem release + `RE-VERIFICAR` em `PESQUISA.md` §1.11 |
| Probing gastar crédito do usuário | custo inesperado | opt-in + orçamento + só modelo selecionado (`AGENTS.md` §2) |
| API de terminal inexistente | ferramenta fraca | já resolvido: `Process` + output pane (§2.5) |