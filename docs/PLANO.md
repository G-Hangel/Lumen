## Fase 9 — Compressão e otimização avançada de contexto

**Objetivo:** refinamento e controle pelo usuário do sistema de compactação e hierarquia de contexto.

### Tarefas

- [ ] Ajuste de sensibilidade e orçamento de tokens para compactação automática de histórico.
- [ ] Sumarização incremental de turnos antigos com retenção de referências cruciais (arquivos, decisões, erros).
- [ ] Indicador visual na UI de quando o histórico foi compactado (`history_compacted`).
- [ ] Política de exclusão ou poda de anexos e menções antigas (`@arquivo`) em conversas longas.

### AC

- Conversas longas (200+ turnos) compactam sem estouro de janela de contexto do modelo.
- O indicador de compactação aparece de forma acessível na UI.
- O usuário pode inspecionar ou redefinir a compactação.

### CN

- Histórico corrompido ou truncado durante a compactação → **recuperação graciosa**, sem perder a sessão ativa.
- Orçamento excedido por um único turno gigante → **truncamento seguro** com aviso.

### Commit

`feat: add advanced context compression and history summarization`

EOF