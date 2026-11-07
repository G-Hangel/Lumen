# STATUS — Lumen

Atualize este arquivo ao concluir cada fase. É a primeira coisa que o próximo
agente lê para saber onde parou.

## Estado atual

**Planejamento concluído. Implementação NÃO iniciada.**

| Fase | Estado |
| --- | --- |
| Planejamento (pesquisa, arquitetura, plano) | **concluído** |
| Fase 0 — Spike de toolchain e UI | não iniciada |
| Fase 1 — Núcleo de domínio | não iniciada |
| Fase 2 — BYOK | não iniciada |
| Fase 3 — UI do chat | não iniciada |
| Fase 4 — Ferramentas e segurança | não iniciada |
| Fase 5 — Loop do agente e modos | não iniciada |
| Fase 6 — Skills | não iniciada |
| Fase 7 — Personalização visual | não iniciada |
| Fase 8 — Hardening e empacotamento | não iniciada |

## Próximo passo exato

Abrir `docs/PLANO.md` e executar a **Fase 0**. Ela é bloqueante: prova o ciclo
restore → build → instalar → abrir no VS → WebView2 renderizando, antes de
qualquer feature.

Primeira tarefa da Fase 0: confirmar no nuget.org as versões de
`Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools`,
`Microsoft.Web.WebView2` e `Microsoft.NETFramework.ReferenceAssemblies` que
restauram e compilam juntas em `net48`.

# STATUS — Lumen

Atualize este arquivo ao concluir cada fase. É a primeira coisa que o próximo
agente lê para saber onde parou.

## Estado atual

**Planejamento concluído. Implementação NÃO iniciada.**

| Fase | Estado |
| --- | --- |
| Planejamento (pesquisa, arquitetura, plano) | **concluído** |
| Fase 0 — Spike de toolchain e UI | não iniciada |
| Fase 1 — Núcleo de domínio | não iniciada |
| Fase 2 — BYOK | não iniciada |
| Fase 3 — UI do chat | não iniciada |
| Fase 4 — Ferramentas e segurança | não iniciada |
| Fase 5 — Loop do agente e modos | não iniciada |
| Fase 6 — Skills | não iniciada |
| Fase 7 — Personalização visual | não iniciada |
| Fase 8 — Hardening e empacotamento | não iniciada |

## Próximo passo exato

Abrir `docs/PLANO.md` e executar a **Fase 0**. Ela é bloqueante: prova o ciclo
restore → build → instalar → abrir no VS → WebView2 renderizando, antes de
qualquer feature.

Primeira tarefa da Fase 0: confirmar no nuget.org as versões de
`Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools`,
`Microsoft.Web.WebView2` e `Microsoft.NETFramework.ReferenceAssemblies` que
restauram e compilam juntas em `net48`.

## O que já foi feito no ambiente

- Repositório git inicializado em `main` (sem remote).
- Documentos de planejamento escritos e **commitados** (veja `git log`).
- **Probe de build real:** .NET SDK 11.0.100-rc.3 compila `net8.0-windows` +
  `UseWPF=true` com 0 erros / 0 avisos em 8,77 s. Probe removida depois.
  → falta repetir o probe para `net48` (tarefa da Fase 0).
- Inspeção completa do VS 18 Insiders: runtime, MSBuild, VSIXInstaller,
  WebView2, contratos do Copilot, manifesto de extensão real.
- Duas pesquisas externas concluídas (SDK de extensibilidade e APIs BYOK),
  consolidadas em `docs/PESQUISA.md` com fontes.
- **Workload `Microsoft.VisualStudio.Workload.VisualStudioExtension` instalado**
  na instância Insiders (verificado por `vswhere`).

## Decisões do usuário registradas

1. Estratégia: **chat próprio independente** (não estender o Copilot nativo).
2. Nome: **Lumen**.
3. Aprovação no Agent: **auto-editar dentro da solução com diff + undo;
   confirmar comandos e escritas fora do workspace**.
4. Modelo de extensão: **VSSDK net48 + WebView2** (escolhido após a pesquisa
   mostrar as restrições do Remote UI e a ausência dos pacotes 18.x no nuget.org).
5. Workload de desenvolvimento de extensão: **autorizado e instalado**.

## Como retomar

```bash
cd "C:/Users/Hanjel/Documents/Extensão"
git log --oneline -5
```

Ler `AGENTS.md` → `docs/PLANO.md` → este arquivo → `docs/BLOQUEIOS.md`.

## Decisões do usuário registradas

1. Estratégia: **chat próprio independente** (não estender o Copilot nativo).
2. Nome: **Lumen**.
3. Aprovação no Agent: **auto-editar dentro da solução com diff + undo;
   confirmar comandos e escritas fora do workspace**.
4. Modelo de extensão: **VSSDK net48 + WebView2** (escolhido após a pesquisa
   mostrar as restrições do Remote UI e a ausência dos pacotes 18.x no nuget.org).
5. Workload de desenvolvimento de extensão: **autorizado instalar**.

## Como retomar

```bash
cd "C:/Users/Hanjel/Documents/Extensão"
git log --oneline -5
```

Ler `AGENTS.md` → `docs/PLANO.md` → este arquivo → `docs/BLOQUEIOS.md`.