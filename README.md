# Lumen

Chat de IA para **Visual Studio Community 2026 (18.x)** com **BYOK** (bring your
own key), modos **Agent / Ask / Plan**, ferramentas de arquivo e terminal,
sistema de **Skills** e personalização visual.

> **Status: fases 0–9 implementadas; endurecimento e entrega em andamento.**
> O plano executável está em [`docs/PLANO.md`](docs/PLANO.md) e o estado real em
> [`docs/STATUS.md`](docs/STATUS.md). Regras de execução: [`AGENTS.md`](AGENTS.md).

## Documentos

| Arquivo | Para quê |
| --- | --- |
| [`AGENTS.md`](AGENTS.md) | Regras obrigatórias de execução autônoma. **Leia primeiro.** |
| [`docs/PLANO.md`](docs/PLANO.md) | 9 fases (0–8) com critérios de aceite e controles negativos |
| [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md) | Desenho técnico e fatos verificados no ambiente |
| [`docs/PESQUISA.md`](docs/PESQUISA.md) | Endpoints, campos JSON e APIs exatos, com fontes |
| [`docs/STATUS.md`](docs/STATUS.md) | Onde a execução parou |
| [`docs/BLOQUEIOS.md`](docs/BLOQUEIOS.md) | Bloqueios conhecidos e pré-requisitos |

## O que o Lumen faz

- **BYOK com descoberta automática**: cola a chave, ele lista os modelos e
  detecta **visão**, **raciocínio avançado** e **tool calling** — mostrando a
  origem de cada capacidade (metadado nativo, registry, heurística ou probe).
- **Três modos**: `Agent` (executa), `Ask` (read-only), `Plan` (só planeja).
- **Ferramentas**: ler, criar e editar arquivos com diff e undo em lote;
  executar comandos em cmd, PowerShell e bash.
- **Skills** no formato `SKILL.md` (agentskills.io) — compatível com as skills
  que o VS 2026 e o Claude já leem.
- **Personalização visual** com temas em CSS variables que seguem o tema do VS.

## Instalação e primeiro uso

1. **Instale o VSIX** de `src/Lumen.Vsix/bin/Release/net48/Lumen.Vsix.vsix`
   (duplo clique, ou `VSIXInstaller.exe /rootSuffix:Exp /instanceIds:<id>` para a
   instância experimental). Requer VS Community 2026 (18.x) amd64 e WebView2 Runtime.
2. **Abra a janela** `Exibir → Lumen`.
3. **Cole sua chave** no painel Provedores, escolha o tipo de provedor e a URL
   base. A URL deve ser HTTPS; HTTP é aceito somente em `localhost`/loopback.
   Nenhuma requisição é feita antes dessa configuração explícita.
4. **Escolha o modelo** descoberto. As badges mostram visão, raciocínio e tools;
   a origem de cada capacidade aparece no painel. Para Azure, informe os nomes
   de deployments manualmente (limitação B8).
5. **Converse**. Em `Ask` e `Plan` nada é escrito nem executado. Em `Agent`,
   leitura é livre; criar/editar arquivos dentro da solução aplica um diff com
   undo em lote; escrita fora da solução e qualquer comando exigem confirmação.
6. **Opcional:** use `/nome` para acionar uma Skill, `@arquivo` para anexar
   contexto, e o painel Aparência para temas e o painel Agentes para delegação.

A chave fica no Windows Credential Manager (com fallback DPAPI), nunca em
settings, log ou pacote. Não há telemetria: nenhum dado sai do computador além
das chamadas ao provedor que você configurou.

## Decisões já tomadas (não reabrir)

| Item | Decisão |
| --- | --- |
| Nome | **Lumen** |
| Estratégia | Chat próprio independente — não estender o Copilot Chat nativo, não depender de sign-in GitHub |
| Modelo de extensão | **VSSDK clássico, `net48`, in-process** |
| UI | **WPF + WebView2** (HTML/CSS/JS local, sem CDN) |
| Aprovação no Agent | leitura livre · edit dentro da solução automático com diff + undo em lote · escrita fora do workspace e **todo** comando de terminal exigem confirmação |
| Plan / Ask | nunca escrevem, nunca executam comando |
| Créditos do usuário | probing de capacidades é opt-in e orçado; testes usam mocks; nenhuma chamada paga sem autorização explícita |

### Por que VSSDK clássico e não o modelo out-of-process

O modelo moderno (`VisualStudio.Extensibility`) tem **Remote UI restrito**:
MVVM-only, sem code-behind, sem controles de terceiros, e a interop JS↔C# com
WebView2 ficou sem solução confirmada. Além disso, os pacotes 18.x dele **não
estão no nuget.org** — só vêm com um workload do VS.

O VSSDK clássico restaura do nuget.org, é **totalmente suportado no 18.x**, e dá
liberdade total de UI. O WebView2 já renderiza em processo separado, então a UI
pesada não compete com a UI thread do VS. Detalhe e fontes em
[`docs/ARQUITETURA.md`](docs/ARQUITETURA.md) §2.1.

## Ambiente alvo verificado

- Visual Studio Community 2026 **Insiders 18.12.12328.396**, instanceId `25e762c6`
  (`C:\Program Files\Microsoft Visual Studio\18\Insiders`) — expira em 2027-01-26
- `devenv.exe` em **.NET Framework 4.7.2** (o shell do VS 2026 **não** migrou para
  .NET 10; isso vale só para componentes out-of-process)
- Hosts out-of-process .NET 8.0.33 e 10.0.14 embutidos no VS
- **WebView2 Runtime 156.0.4314.58** presente
- SDK local: .NET 11.0.100-rc.3 — **testado**: compila `net8.0-windows` + WPF
  com 0 erros e 0 avisos
- Disco `C:` com ~14,1 GB livres em 2026-11-17 — conferir antes de builds grandes

## Repositório

Git em `main` com `origin` configurado pelo usuário. `gh` CLI não está instalado;
criar remote, dar push e abrir PR são decisões e ações do usuário.