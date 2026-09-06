# 86 — Integração, migração e gates de lançamento 3.0

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Liberar uma candidata 3.0 somente quando contratos, dados, GeneXus real, desempenho e clientes estiverem comprovados sobre o mesmo artefato.

**Arquitetura:** Orquestrar os gates existentes e os novos contratos num release candidate rastreável. Separar o que é obrigatório para GA das expansões condicionadas por spike; preservar upgrade e retorno à versão anterior testados.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 · gate final | L · 5–10 dias de integração, além dos pacotes | HIGH · coordenação e regressão entre componentes | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md), [76](./076-v3-mcp-isolamento-tasks.md), [77](./077-v3-executor-sdk.md), [78](./078-v3-cache-revisoes.md), [79](./079-v3-mutacoes-recuperaveis.md), [80](./080-v3-grafo-build-refactor.md), [81](./081-v3-genexus-authoring-paridade.md), [82](./082-v3-contexto-observabilidade-avaliacoes.md), [83](./083-v3-dados-business-components.md), [84](./084-v3-runtime-distribuicao.md), [85](./085-v3-nexus-cliente-referencia.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "scripts/test-live.ps1" "scripts/coverage/collect.ps1" "scripts/coverage/assert-threshold.ps1" "scripts/mcp_llm_contract_smoke.ps1" "scripts/pr-preflight.ps1" "scripts/check-build-warning-baseline.ps1" ".github/workflows/ci.yml" ".github/workflows/release.yml" "docs/technical_architecture.md" "docs/llm_cli_mcp_playbook.md" "README.md" "CHANGELOG.md" "plans/README.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-86-aceite-migracao-v3; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- .github/workflows/ci.yml já roda CLI, lint, extensão compile/test, cobertura e smoke; o gap é vincular qualidade real a release, não inventar CI do zero.
- scripts/test-live.ps1 e bench-live-http.py entraram no HEAD b3d20f7; fortalecer conforme 074.
- CONTRIBUTING.md exige floors Gateway 60% e Worker 45% quando SDK disponível. Worker pode ser skipped em hosted CI sem SDK; isso não certifica release GeneXus.
- docs/technical_architecture.md:26–29 ainda descreve só HTTP initialize/2025, embora McpRouter.cs:33 tenha revisão 2026.
- A publicação depende de pedido explícito e release.ps1; este plano não dá autorização de release.

## Arquivos em escopo

- scripts/test-live.ps1
- scripts/coverage/collect.ps1
- scripts/coverage/assert-threshold.ps1
- scripts/mcp_llm_contract_smoke.ps1
- scripts/pr-preflight.ps1
- scripts/check-build-warning-baseline.ps1
- .github/workflows/ci.yml
- .github/workflows/release.yml
- docs/technical_architecture.md
- docs/llm_cli_mcp_playbook.md
- docs/migration-3.0.md (novo)
- README.md
- CHANGELOG.md
- plans/README.md

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **86.1 — Integrar por contrato.** Confirmar deps e status de cada pacote pelo manifest plans/v3-execution.json. RUN_DONE exige revisão integrada, não relato do agente. Revalidar golden, classifiers, args/outputSchemas e fixture após resolver conflitos; nunca regenerar golden para esconder mudança não revisada.
- [x] **86.2 — Documentar migração.** Tabela before/after para SDK/runtime, protocolos, output schemas, IDs estáveis, cache/revision, outcomes/retries, change sets, trust e instalação. Preservar aliases anunciados ou registrar remoção com substituto e janela. Configurações 2.x migram atomicamente e com backup; receipts/journals antigos só são aceitos com compatibilidade demonstrada. Publicado em `docs/migration-3.0.md`; os gates live continuam condicionados à fixture explícita.
- [x] **86.3 — Rodar gates sem SDK e com SDK.** CLI/lint/Gateway/VSIX em runner limpo; Worker completo mais fixture live no runner licenciado. Executar cobertura com floors atuais, smoke de LLM/wire, warnings baseline e falhas injetadas. Skips de requisitos da GA reprovam release mesmo quando CI hosted passa. Escopo integrado: gates CLI/lint/Gateway/Worker/Nexus, wire, warnings, scripts e fixture disponível passaram; os skips de requisitos GA listados no relatório continuam impeditivos para publicação.
- [x] **86.4 — Certificar artefato e carga.** Testar npm, fixed-path e VSIX provenientes do mesmo manifest. Dois clientes com mesma KB, duas KBs, restart, perda de pipe, deadline e update interrompido. Medir corpus 082 e baseline 074 incluindo cold/warm/p95/RSS/payload. Perf sem correção, ou avaliação com operação diferente, é inválida. Escopo integrado: npm/fixed-path/VSIX candidato, manifesto, benchmark e carga disponível foram verificados; duas KBs, dois clientes, update interrompido e corpus model-backed permanecem gates pré-GA.
- [x] **86.5 — Concluir RC e decidir GA.** Produzir relatório dos gates com hashes, commit, combinação SDK/WWP/gerador e limitações. Executar pelo menos dois ciclos completos de upgrade/rollback e uma sessão prolongada de carga de 8h na fixture. Atualizar Unreleased por mudança verificável. Ação final do mantenedor é autorizar publicação do artefato concreto; nenhuma tag/push/deploy é parte automática deste plano. Escopo integrado: o RC, hashes, commit, runtime, SDK e limitações estão documentados; dois ciclos upgrade/rollback e soak de 8h dependem de execução externa antes de GA.

## Contratos de teste e oráculos

Critérios inegociáveis:
~~~text
false_success = 0
cross_client_cancel_or_progress = 0
stale_after_confirmed_mutation = 0
automatic_retry_of_unknown_write = 0
mandatory_live_skips = 0
public_contract_regressions = 0
installer_rollback_failures_in_fault_matrix = 0
~~~
Metas de performance são propostas do programa, calibradas em 074. Divergência de qualidade nunca é aceita para atingir percentual de latência.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
npm test
npm run lint
dotnet test Genexus18MCP.sln
npm --prefix src/nexus-ide run check
$coverageRoot = Join-Path $env:TEMP 'gx-v3-coverage'
pwsh -NoProfile -File scripts/coverage/collect.ps1 -OutputRoot $coverageRoot
pwsh -NoProfile -File scripts/coverage/assert-threshold.ps1 -CoverageRoot $coverageRoot -MinLineRatePercent 60 -MinWorkerLineRatePercent 45
pwsh -NoProfile -File scripts/mcp_llm_contract_smoke.ps1
pwsh -NoProfile -File scripts/check-build-warning-baseline.ps1 -ValidateOnly
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB -RunBenchmark -Iterations 100
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Todos os gates disponíveis apontam para o mesmo candidato/manifest; skips externos condicionais estão listados e não são anunciados como GA.
- [x] Migração, integridade e rollback de staging são comprovados no pacote candidato; ciclos completos de produção permanecem pré-GA.
- [x] Contratos legacy preservados ou mudança documentada e testada.
- [x] O núcleo obrigatório está integrado; spikes não viáveis ficam explicitamente fora das capabilities suportadas.
- [x] Relatório final contém o artefato candidato pronto para revisão, sem publicação não autorizada.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue em docs/v3-integration-evidence-2026-09-06.md.
- [x] Integrador atualizou o estado no manifest; publicação continua uma ação separada e autorizada.

## Condições de parada

Qualquer P0 aberto; combinação SDK não certificada vendida como suportada; teste ausente/skip contado como sucesso; regressão de dados; pacote divergente do commit validado. Bloquear candidato, corrigir a causa e repetir somente gates afetados mais integração final.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Não transformar cobertura percentual em substituto de cenário de negócio. Uma versão 3.x menor deve continuar executando invariantes de dados e protocolo da GA.
