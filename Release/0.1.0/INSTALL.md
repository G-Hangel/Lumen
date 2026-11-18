# Lumen 0.1.0 — instalação

## Conteúdo
- `Lumen-0.1.0.vsix` — pacote da extensão (SHA-256 conferido na build).
- `deploy-experimental.ps1` — script de instalação/verificação/remoção.
- `CHANGELOG.md` — changelog da versão.

## Pré-requisitos
- Visual Studio Community 2026 (18.x) ou Insiders, com a extensão **Lumen** já em desenvolvimento.
- Instância experimental identificada por `InstanceId` (ex.: `25e762c6`).

## Instalar
```powershell
cd Release\0.1.0
powershell.exe -NoProfile -File deploy-experimental.ps1 -Action Install -InstanceId 25e762c6
```

## Verificar
```powershell
powershell.exe -NoProfile -File deploy-experimental.ps1 -Action Verify -InstanceId 25e762c6
```

## Remover
```powershell
powershell.exe -NoProfile -File deploy-experimental.ps1 -Action Uninstall -InstanceId 25e762c6
```

## Usar
Abrir **Exibir → Lumen** no Visual Studio. Configurar o provedor BYOK, selecionar o modelo e
escrever no chat. O loop de ferramentas é automático: se atingir o limite de segurança o turno
pausa e um botão **Continuar** permite retomar.

## Limites do turno (automáticos)
- 64 rodadas · 262144 tokens de saída · 256 invocações de ferramenta.
- Resultado acumulado por turno: 131072 caracteres.
- Deadline global: 10 minutos.