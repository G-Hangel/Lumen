# AGENTS.md — Regras para o agente implementador

Este repositório está sendo construído por um agente de IA de forma autônoma.
Estas regras são obrigatórias e valem para qualquer agente que trabalhe aqui.

## 0. Ordem de leitura

1. `docs/PLANO.md` — fases, tarefas e critérios de aceite. **Execute na ordem.**
2. `docs/ARQUITETURA.md` — desenho técnico e fatos já verificados no ambiente.
3. `docs/PESQUISA.md` — endpoints, campos JSON e APIs exatos.
4. `docs/STATUS.md` — onde a execução parou.

Não re-derive o que já está verificado nesses arquivos. Se um fato lá estiver
errado, corrija-o com evidência e anote a fonte.

## 1. Escopo travado — decisões do usuário, não reabrir

| Item | Decisão |
| --- | --- |
| Nome | **Lumen** |
| Estratégia | **Chat próprio independente.** Não estender o Copilot Chat nativo do VS. |
| Alvo | Visual Studio Community 2026 (18.x) |
| Modos | **Agent**, **Ask**, **Plan** |
| BYOK | Descoberta automática de modelos + capacidades (visão, raciocínio, tool calling) |
| Ferramentas | Ler / editar / criar arquivos; executar cmd, PowerShell e bash |
| Extras | Sistema de Skills e personalização visual |

**Política de aprovação (modo Agent):**

- Leitura e busca: livre, sem confirmação.
- Criar/editar arquivo **dentro da solução**: aplica automaticamente, sempre com
  diff visível e undo em lote.
- Escrita **fora** do workspace: exige confirmação.
- **Qualquer** comando de terminal: exige confirmação, com opção
  "sempre permitir nesta sessão".
- Modo **Plan**: nunca escreve nada, nunca executa comando. Só produz o plano.
- Modo **Ask**: read-only. Pode ler arquivos, nunca alterar.

## 2. Dinheiro e credenciais — rigoroso

- **Nunca gastar créditos do usuário sem confirmação explícita.** Isso inclui
  probing de capacidades, testes de integração contra APIs pagas e chamadas
  repetidas em loop.
- Testes automatizados usam **mocks/fakes**. Integração real só com chave de
  teste fornecida pelo usuário e autorização por escrito na conversa.
- O probing de capacidades na UI é **opt-in**, com orçamento visível, e só sobre
  o modelo selecionado — nunca sobre a lista inteira.
- Chaves vão para o **Windows Credential Manager** (ou DPAPI). Nunca em plaintext,
  nunca em settings JSON, nunca em log, nunca em commit. Redija a chave em
  qualquer mensagem de erro.

## 3. Execução contínua com checkpoints

- Trabalhe fase por fase. Não pule critério de aceite.
- Ao concluir cada fase: build limpo + testes verdes + commit + atualizar
  `docs/STATUS.md`.
- **Bloqueio:** não invente solução. Registre em `docs/BLOQUEIOS.md` com evidência
  (comando, saída, arquivo:linha) e siga para a próxima tarefa independente.
  Só pare e pergunte se o bloqueio impedir todo o progresso.
- **Sem polling em laço.** Build/teste longo: uma linha de estado e checagens em
  marcos, nunca a cada 1–2 minutos.
- Não declare uma fase concluída com teste falhando ou implementação parcial.

## 4. Verificação honesta

- **Todo verificador precisa de controle negativo.** Prove que ele *rejeita* o
  caso ruim antes de reportar sucesso:
  - o detector de capacidades marca corretamente um modelo **sem** visão;
  - a camada de segurança **bloqueia** um comando perigoso;
  - o parser de streaming sobrevive a evento desconhecido e a JSON truncado;
  - o confinamento de path **rejeita** `../../` para fora do workspace.
- "Compila" não é "funciona". Exercite a UI na **experimental instance** do
  Visual Studio antes de dizer que a feature está pronta. Se não conseguir
  testar a UI, diga isso explicitamente em vez de afirmar sucesso.
- Nunca reporte 100% de aprovação sem ter rodado o controle negativo.

## 5. Convenções de código

- C# e .NET. Identificadores em **inglês**. Texto de UI e docs em **PT-BR e EN**
  via resource files (o VS instalado está em pt-BR).
- Sem comentários óbvios. Comente apenas o **WHY** não-óbvio: invariante sutil,
  constraint escondida, workaround de bug específico.
- Sem abstração prematura, sem feature flag, sem shim de compatibilidade.
- Não trate erro de cenário impossível. Valide só nas fronteiras: input do
  usuário e APIs externas.
- **Segurança obrigatória:**
  - Nunca monte linha de comando por concatenação de string vinda do modelo.
    Use lista de argumentos.
  - Canonicalize todo path e confine ao workspace antes de ler/escrever.
  - Blocklist de comandos é defesa em profundidade, **não** substitui a
    confirmação do usuário.
  - Sanitize HTML/Markdown renderizado na UI. Nada de executar script vindo do
    modelo.

## 6. Git

- Commit pequeno por fase concluída. Mensagem no imperativo, foco no porquê.
- Nunca `--no-verify`, nunca force push, nunca amend de commit já publicado.
- Nunca commitar chaves, `*.vsix`, `bin/`, `obj/`, `.env`.
- **Não criar remote nem dar push** sem o usuário pedir explicitamente.

## 7. Proibições

- Não depender de sign-in GitHub nem de assinatura Copilot.
- Não usar APIs `Microsoft.VisualStudio.Copilot.Internal.*` ou
  `UnstableInternalApi.*` — são instáveis e não são contrato público.
- Não inventar endpoint ou campo JSON. `docs/PESQUISA.md` é a fonte; se precisar
  de algo novo, verifique ao vivo e registre a URL da fonte.
- Não criar documentos além dos previstos no plano.
- Não implementar fora da fase atual "porque já estou aqui".