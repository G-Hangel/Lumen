# Changelog — Lumen

Formato: [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
Versões seguem `MAJOR.MINOR.PATCH` do manifesto do VSIX.

## [0.1.0] — 2026-11-17

### Adicionado

- Chat BYOK para Visual Studio Community 2026 (18.x) com descoberta automática
  de modelos e capacidades (visão, raciocínio, tool calling).
- Modos **Agent**, **Ask** e **Plan** com política de permissões aplicada no host.
- Ferramentas de leitura, criação/edição de arquivos com diff e undo em lote,
  e execução de cmd/PowerShell/bash com aprovação.
- Skills no formato `SKILL.md`, com descoberta, ativação automática por descrição
  e exemplos embarcados.
- Personalização visual com temas em CSS variables, prévia ao vivo,
  importação/exportação e sincronia com o tema do VS.
- Delegação limitada a subagentes locais descobertos em `.lumen/agents/`, com
  painel de acompanhamento e validação de modo.
- Verificadores automatizados na instância experimental com provedor loopback,
  sem chamadas pagas.
- Exclusão de provedores salvos, com confirmação explícita e remoção da
  credencial correspondente; o histórico de conversas é preservado.

### Corrigido

- O command set agora é empacotado no VSIX (`Menus.cto`); sem ele o comando
  `View.Lumen` não era registrado e a janela nunca abria.

### Limitações conhecidas

- Inventário de deployments Azure ainda exige nomes informados
  (B8, aceito pelo usuário).
- Subagentes não fazem chamadas de modelo próprias nem gerenciam orçamento;
  veja `docs/STATUS.md` (Fase 9).
- Ícone e imagem de preview do marketplace ainda pendentes (B12).
