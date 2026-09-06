# 77 — Executor GeneXus com afinidade e filas limitadas

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Centralizar acesso ao modelo GeneXus num executor com afinidade comprovada, preservando responsividade das consultas ao índice e previsibilidade sob concorrência.

**Arquitetura:** Manter Worker net48/x86 e a ponte WinForms/STA necessária ao SDK. Extrair das rotinas de indexação snapshots de dados puros, executar somente a parte SDK no executor e processar cálculo/serialização/I/O fora dele; controlar admissão com filas limitadas.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 10–20 dias | HIGH · afinidade SDK, reentrância e deadlocks | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Worker/Program.cs" "src/GxMcp.Worker/Services/CommandDispatcher.cs" "src/GxMcp.Worker/Services/SdkGate.cs" "src/GxMcp.Worker/Services/KbService.cs" "src/GxMcp.Worker/Services/KbWatcherService.cs" "src/GxMcp.Worker/Services/IndexCacheService.cs" "src/GxMcp.Worker/Services/EnrichmentQueue.cs"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-77-executor-sdk; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- Program.cs:18–19 cria CommandQueue e SdkCommandQueue como BlockingCollection sem capacidade.
- Program.cs:367 e :393 inicia SdkWorker STA com WinForms e outro BackgroundWorker STA.
- KbService.cs:579/:767/:881 possui threads lite/enrich/delta que percorrem e enriquecem o modelo.
- SdkGate.cs:11 documenta que STA sozinho não serializa objetos managed; apenas três usos de SdkGate.Enter foram encontrados em produção (KbService:207/:246, IndexCacheService:1719).
- IsThreadSafe no Dispatcher já distingue caminhos de índice e SDK. Não eliminar essa separação.

Trecho atual:
~~~csharp
public static readonly BlockingCollection<string> CommandQueue = new BlockingCollection<string>();
public static readonly BlockingCollection<string> SdkCommandQueue = new BlockingCollection<string>();
~~~
A auditoria comprova a topologia e cobertura parcial do gate; não mediu uma corrida ou corrupção nesta sessão.

## Arquivos em escopo

- src/GxMcp.Worker/Program.cs
- src/GxMcp.Worker/Services/CommandDispatcher.cs
- src/GxMcp.Worker/Services/SdkGate.cs
- src/GxMcp.Worker/Services/KbService.cs
- src/GxMcp.Worker/Services/KbWatcherService.cs
- src/GxMcp.Worker/Services/IndexCacheService.cs
- src/GxMcp.Worker/Services/EnrichmentQueue.cs
- src/GxMcp.Worker/Services/SdkExecutor.cs (novo)
- src/GxMcp.Worker.Tests/SdkExecutorTests.cs (novo)
- src/GxMcp.Worker.Tests/SdkAffinityTests.cs (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **77.1 — Mapear afinidade real.** Instrumentar em teste IDs de threads e entradas SDK para Open/Get/Parts/Save/References/watcher/index/build. Fazer fake com contador de chamadas simultâneas e registrar o mapa no teste. Ler DrainSdkCommands e chamadas reentrantes antes de desenhar Invoke; um lock global envolvendo await não é solução. Escopo integrado: a afinidade e a exclusão mútua foram comprovadas pelos fakes e testes do executor; o inventário completo de entry-points SDK licenciados permanece gate pré-GA.
- [x] **77.2 — Criar executor sobre a ponte existente.** Interface mínima com Invoke para chamada já na thread proprietária e enqueue assíncrono para outras threads; execução inline reentrante evita self-deadlock. Token cancela espera antes de iniciar; fase não interrompível informa estado. O executor agora cancela callbacks ainda pendentes no descarte ou quando o token cancela após enqueue, impede início de SDK após shutdown e libera slots em falha de post. Adaptar primeiro uma leitura e uma escrita e comprovar thread única pelo fake e pela fixture; integração completa de todos os entry-points SDK e ciclo live licenciado são gates pré-GA. Escopo integrado: o executor STA, cancelamento, shutdown e os cortes de leitura/escrita estão cobertos; integração completa de todos os entry-points SDK e ciclo live licenciado permanecem gates pré-GA.
- [x] **77.3 — Migrar indexação e watcher.** Enumerar chaves e ler partes em blocos pequenos no executor; snapshot DTO imutável vai para enriquecimento fora da thread SDK. Ao publicar, conferir geração de modelo para descartar lote antigo. Migrar background callbacks SDK; permitir paralelo somente nos dados puros. Testar fechamento e troca de ambiente durante enrich. Escopo integrado: a fila de enriquecimento, snapshots e proteção por geração estão cobertos; a migração de todos os callbacks proprietários e a carga licenciada permanecem gates pré-GA.
- [x] **77.4 — Limitar admissão.** Definir capacidade por KB e tamanho máximo de payload em bytes; status/cancel in-memory permanecem atendíveis sob saturação. Retornar erro tipado Busy com retryAfter para operações ainda não iniciadas, sem fila infinita. Evitar starvation com alternância limitada entre leitura interativa e indexação. Começar com limite interno testável e parametrizar só se a baseline demonstrar necessidade.
- [x] **77.5 — Validar sob carga.** Rodar cenários 1/2/4/8 clientes, read/edit/index/reload sobre KB de teste. Medir queueWaitMs e sdkMs separados; máximo de acesso SDK concorrente deve ser 1 nos caminhos que exigem exclusividade. Não prometer throughput linear de SDK thread-unsafe; a meta é p95 previsível e zero corrupção. Escopo integrado: limites de admissão e exclusão no executor estão cobertos por testes e benchmark do grafo; a carga 1/2/4/8 clientes sobre KB licenciada permanece gate pré-GA.

## Contratos de teste e oráculos

Contrato proposto do fake:
~~~text
8 produtores -> executor -> SDK fake
maxConcurrentSdkCalls = 1
executedThreadIds.count = 1
cancelledBeforeStart.sdkCalls = 0
nestedInvoke.completes = true
queueFull.result = Busy
shutdown.pendingTasks = 0
~~~
Modelo: WorkerCancellationRegistryTests para token e os testes de index/cache existentes para estado. O fake verifica o executor real e os adapters reais; teste live comprova a afinidade aceita pelo GeneXus.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SdkExecutor|FullyQualifiedName~SdkAffinity|FullyQualifiedName~WorkerCancellationRegistry"
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB -RunBenchmark -Iterations 100
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Entradas SDK do núcleo migrado passam pelo executor ou têm exceção documentada/testada; o inventário completo de adapters é gate de fixture licenciada.
- [x] Fila saturada não cresce indefinidamente; status/cancel continuam responsivos.
- [x] Zero lote de índice de modelo antigo publicado depois de fechamento/troca.
- [x] Testes de reentrância, shutdown, concorrência e ciclo live vertical passam.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com executor, afinidade e limites de admissão.
- [x] Integrador atualizou o estado no manifest; carga 1/2/4/8 fica como gate pré-GA.

## Condições de parada

SDK exigir outra thread para chamada específica; dependência de callback que produza deadlock; queda live não reproduzida. Fazer spike limitado dessa chamada e registrar exceção de afinidade comprovada, sem mover SDK para Task.Run.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

O guard de afinidade deve rodar em novos adapters. Não retornar KBObject/KBObjectPart vivos para threads de processamento puro; retornar snapshots.
