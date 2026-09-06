# Migração para MCP 3.0

Este documento descreve o contrato de migração que já está implementado no
checkout e separa os gates que ainda dependem de uma fixture GeneXus licenciada.
Não há migração automática de uma KB nem publicação implícita.

| Área | Linha 2.x | Linha 3.0 | Verificação |
| --- | --- | --- | --- |
| Gateway | .NET 8 | .NET 10 Windows | build e suíte Gateway |
| Worker | .NET Framework 4.8 x86/STA | preservado | suíte Worker com SDK |
| CLI | Node 20+ | Node 22+ (CI Node 24) | `npm ci`, lint e testes |
| MCP | HTTP/stdio 2025 | 2025 compatível + 2026 metadata/tasks | contratos de discovery/HTTP/tasks |
| IDs | cancelamento frouxo | tokens JSON-RPC e tasks isolados por sessão/`Mcp-Client-Id` | request isolation tests |
| Operações | retry por transporte | política por efeito e recuperação de outcome unknown | idempotência Gateway/Worker/Nexus |
| Build de KB | `build` ambíguo para escopo global | `build_all` incremental global com evidência, fallback MSBuild e `ReorgRequired` explícito | contratos Gateway/Worker + testes BuildAll |
| Cache | invalidação ampla | escopo por KB, alvo e dependência | semantic-cache tests |
| Contexto | 360 sem orçamento explícito | hash, orçamento UTF-8, cursor e recursos endereçáveis | ContextBundleService tests |
| Dados | records SQL sem semântica BC explícita | `dataAccess=typed_sql`, `businessRulesExecuted=false` | TransactionRecords tests |
| Distribuição | extração direta | manifest, staging, probe, backup e rollback | install-contract tests |
| Confiança | startup/config implícitos | Workspace Trust antes de spawn ou persistência | Nexus test suite |

## Configuração e reversão

Instalações 3.0 preservam `config.json`, `config.local.json`, `auth.json` e
`credentials.json`, validam o manifesto antes da troca e mantêm um backup
`.previous-*` após o swap. Um probe pós-troca que falha restaura o backup e
preserva a instalação que falhou para diagnóstico. Releases legadas sem
manifesto continuam em um caminho de compatibilidade explícito; releases 3.x
falham fechadas quando o manifesto ou checksum está ausente.

O protocolo 2025 permanece disponível para clientes antigos. O cliente pode
negociar metadata/tasks de 2026, mas tasks modernos exigem a capability
declarada e, no HTTP sessionless, o header estável `Mcp-Client-Id`; sem ele a
consulta falha fechada antes de acessar o registro de tasks.

## Geração do artefato

`release.ps1` gera `publish/gxmcp-manifest.json` com versão, commit, protocolos,
schema e hashes dos executáveis Gateway/Worker antes de criar o ZIP. O mesmo
manifesto é verificado pelo workflow de release e pelo instalador. O VSIX e o
ZIP ainda precisam ser certificados juntos em um runner com a fixture live antes
de uma decisão de GA.

Antes de propor um RC, valide o manifesto de execução com
`python scripts/validate-v3-plan.py`; o modo final
`python scripts/validate-v3-plan.py --require-ready` só passa quando cada pacote
está marcado como `VERIFIED_INTEGRATED`.

## Gates ainda condicionados ao ambiente

Os testes sem SDK, compilação, testes do Worker com SDK instalado, Nexus, CI,
manifesto e contratos de instalação são executáveis localmente. O smoke live,
Business Component e a carga prolongada exigem uma fixture sintética com
identidade de banco verificada e não são considerados verdes quando a variável
`GXMCP_TEST_KB`/manifesto não está configurada.
