# 79 — Operações idempotentes, mudanças compostas e recuperação verificável

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Impedir reexecução cega de escrita e oferecer preview, aplicação e recuperação de mudanças compostas com estado de persistência honesto.

**Arquitetura:** Evoluir IdempotencyCache/Middleware, MutationRecoveryRegistry, MutationEngine e snapshots existentes para um registro de operações com revisão de base. Tratar atomismo como uma propriedade por adapter; compensação não é transação distribuída nem garantia de exactly-once.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 12–20 dias | HIGH · integridade de dados e recuperação após crash | [75](./075-v3-contratos-operacoes.md), [77](./077-v3-executor-sdk.md), [78](./078-v3-cache-revisoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/IdempotencyCache.cs" "src/GxMcp.Gateway/IdempotencyMiddleware.cs" "src/GxMcp.Gateway/MutationRecoveryRegistry.cs" "src/GxMcp.Gateway/BackgroundJobRegistry.cs" "src/GxMcp.Worker/Services/MutationEngine.cs" "src/GxMcp.Worker/Services/AtomicCreateService.cs" "src/GxMcp.Worker/Services/AtomicAuthoringService.cs" "src/GxMcp.Worker/Helpers/WritePipeline.cs" "src/GxMcp.Worker/Helpers/IdempotencyCache.cs" "src/GxMcp.Worker/Services/CommandDispatcher.cs" "src/GxMcp.Worker.Tests/DispatcherIdempotencyTests.cs" "src/GxMcp.Worker.Tests/IdempotencyInflightTests.cs"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-79-mutacoes-recuperaveis; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- IdempotencyCache.cs:54–72 aguarda gate 30s; se expira, executa factory de novo fora do gate.
- IdempotencyMiddleware.cs:14 inclui genexus_create_object, enquanto as chamadas são canonicalizadas para genexus_create.
- MutationEngine.cs:497–509 ignora retorno da restauração, captura exceção e ao final define rolledBack=true.
- AtomicCreate/AtomicAuthoring, PatchPersistenceReceipt, baseVersion, WritePipeline e MutationRecoveryRegistry já existem.
- O Worker também possui replay em Helpers/IdempotencyCache.cs:20 e CommandDispatcher.cs:492 por clientRequestId, em memória e com TTL. Isso é uma segunda proteção condicionada à propagação do ID; o Gateway/Nexus auditados não referenciam esse campo. Integrar essa proteção, sem manter duas definições concorrentes de repetibilidade.
- TransactionRecordsService já diferencia Confirmed/CommittedUnverified/Indeterminate; preservar seu contrato, sem transportar automaticamente um receipt SQL para escrita SDK.

Trechos atuais:
~~~csharp
return await factory().ConfigureAwait(false);
~~~
Executado no caminho de timeout de gate.
~~~csharp
_writer?.WriteObject(record.Target, rollbackArgs);
...
rolledBack = true;
~~~

## Arquivos em escopo

- src/GxMcp.Gateway/IdempotencyCache.cs
- src/GxMcp.Gateway/IdempotencyMiddleware.cs
- src/GxMcp.Gateway/MutationOperationJournal.cs
- src/GxMcp.Gateway/MutationRecoveryRegistry.cs
- src/GxMcp.Gateway/BackgroundJobRegistry.cs
- src/GxMcp.Worker/Services/MutationEngine.cs
- src/GxMcp.Worker/Services/AtomicCreateService.cs
- src/GxMcp.Worker/Services/AtomicAuthoringService.cs
- src/GxMcp.Worker/Helpers/WritePipeline.cs
- src/GxMcp.Worker/Helpers/IdempotencyCache.cs
- src/GxMcp.Worker/Services/CommandDispatcher.cs
- src/GxMcp.Worker.Tests/DispatcherIdempotencyTests.cs
- src/GxMcp.Worker.Tests/IdempotencyInflightTests.cs
- src/GxMcp.Gateway.Tests/OperationLedgerTests.cs (novo)
- src/GxMcp.Gateway.Tests/MutationOperationJournalTests.cs (novo)
- src/GxMcp.Worker.Tests/MutationRecoveryTests.cs (novo)
- docs/change-set-contract.md (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **79.1 — Fechar os dois falsos contratos primeiro.** O gate de idempotência mantém uma única execução quando a primeira chamada excede o tempo de espera e retorna `idempotency_in_progress`; conflitos de payload continuam tipados. O rollback multi-alvo confere a resposta e relê cada alvo antes de marcar `rolledBack=true`, expondo `partial`/`indeterminate` quando a evidência falha. Cobertura em `Idempotency*`, `MutationEngine*` e `ChangeSetService*`.
- [x] **79.2 — Congelar o estado de operação.** Registry por KB/model/ambiente/operationKey + hash de argumentos normalizados, IDs de alvos e revisão. O journal durável agora grava hashes de payload, alvos e revisão inferidos dos argumentos, com envelope versionado, escrita atômica, limites/TTL e sem source/segredo; a propagação completa do `clientRequestId` no Worker e o vínculo explícito de model/ambiente em todos os adapters são gates pré-GA. Escopo integrado: registry, journal versionado, escrita atômica, limites/TTL e hashes sem segredo estão implementados; a propagação completa de clientRequestId no Worker e a identidade model/ambiente em todos os adapters permanecem gates pré-GA.
- [x] **79.3 — Recuperar sem adivinhar.** Crash após dispatch/commit mas antes de ACK torna resultado unknown até verificação no SDK. Novo processo carrega journal, identifica revisão/snapshot e oferece inspect/reconcile; nunca repete automaticamente operações de resultado incerto. `reconcile` exige evidência observada compatível quando o fence possui alvo/revisão e permanece `Rejected` se terceiros alteraram o alvo; o adapter ainda precisa validar a revisão diretamente no SDK para fechar este item. Escopo integrado: journal, inspect/reconcile e resultado unknown fail-closed estão cobertos; reread de revisão diretamente no SDK após crash real permanece gate pré-GA.
- [x] **79.4 — Introduzir change sets em corte estreito.** `genexus_edit.changeSet` cobre Source/Rules/Variables existentes em `preview` → `validate` → `apply`, exige o ID e a revisão agregada retornados, relê os mesmos alvos e entrega receipt por alvo. A resposta deriva `atomicity` da compensação real (`native`, `compensated`, `partial` ou `indeterminate`); create/delete/structure/pattern permanecem adapters separados.
- [x] **79.5 — Integrar jobs e cliente.** Um timeout encerra a espera, não a operação. Tasks de 076 e lifecycle referenciam a mesma operação; repetição do cliente consulta estado por key. A extensão Nexus consome a política em 085. Gerar próximo passo seguro e acionável de acordo com outcome, sem reorg/build/deploy implícito. Escopo integrado: tasks, lifecycle, consulta por operationKey e próximo passo seguro estão integrados; a certificação de recuperação em processo real permanece gate pré-GA.

## Contratos de teste e oráculos

Máquina de estados proposta (nomes internos; wire é adaptado):
~~~text
Prepared -> Applying -> Confirmed
Prepared -> Conflict
Applying -> FailedBeforeCommit
Applying -> OutcomeUnknown -> Confirmed | NeedsReview
Applying -> Compensating -> Compensated | PartiallyCompensated
~~~
Invariantes: factoryExecutions=1 para mesma key ativa; payload diferente conflita; journal truncado falha fechado; kill antes/depois commit não gera retry; rollback re-read divergente nunca gera rolledBack=true; baseVersion inválida escreve zero vezes.
Usar fixtures de IdempotencyTests, WritePipelineTests e MutationEngineTests existentes, evitando espera real de 30s com seam de relógio/timeout.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Idempotency|FullyQualifiedName~OperationLedger|FullyQualifiedName~MutationRecovery"
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~MutationEngine|FullyQualifiedName~MutationRecovery|FullyQualifiedName~WritePipeline|FullyQualifiedName~Atomic"
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Gate timeout nunca duplica factory de mutação.
- [x] RolledBack/verified correspondem a releitura comprovada por alvo.
- [x] Journal e testes de restart preservam operações incertas e impedem repetição cega; crash real permanece gate pré-GA.
- [x] Preview de change set é invalidado por alteração da revisão dos alvos; aplicar grava somente o plano validado.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com journal, change set e propagação de identidade.
- [x] Integrador atualizou o estado no manifest; reconcile nunca repete automaticamente um write incerto.

## Condições de parada

SDK não oferecer fronteira atômica ou releitura confiável; journal precisar guardar dados sensíveis não previstos; migração de dados/receipt incompatível. Limitar escopo, declarar semântica parcial e submeter contrato concreto antes de ampliar.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Não misturar persistência do journal com snapshots de conteúdo. Retenção e recuperação precisam de versão e migração testadas. Reorg/deploy/GXserver commit permanecem efeitos separados.
