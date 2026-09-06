# 80 — Grafo semântico, refatoração segura e build incremental comprovado

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Reduzir varreduras repetidas e decisões aproximadas, usando identidade GeneXus e referências comprovadas para impacto, refatoração e build.

**Arquitetura:** Evoluir SearchIndex e CallerGraphService para arestas por identidade estável, proveniência e completude. BuildPlan e refactor compartilham essa visão; alteração estrutural ou grafo incompleto força estratégia conservadora, explicitada no resultado.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P1 | L · 15–25 dias | HIGH · referências falsas ou omitidas podem quebrar código gerado | [74](./074-v3-baseline-confiavel.md), [77](./077-v3-executor-sdk.md), [78](./078-v3-cache-revisoes.md), [79](./079-v3-mutacoes-recuperaveis.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Worker/Models/SearchIndex.cs" "src/GxMcp.Worker/Services/CallerGraphService.cs" "src/GxMcp.Worker/Services/IndexCacheService.cs" "src/GxMcp.Worker/Services/IndexStorageEngine.cs" "src/GxMcp.Worker/Services/BuildPlanService.cs" "src/GxMcp.Worker/Services/DefaultFastIncrementalDecision.cs" "src/GxMcp.Worker/Services/BuildService.cs" "src/GxMcp.Worker/Services/EditAndBuildOrchestrator.cs" "src/GxMcp.Worker/Services/RefactorService.cs"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-80-grafo-build-refactor; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- CallerGraphService.cs:57–76 varre Objects.Values com regex por alvo mesmo tendo CalledBy.
- SearchIndex.cs:52 já possui Guid/EntityKey/EntityTypeGuid; aproveitar essa identidade. TypeIndex/DomainIndex/ByNameIndex e índice incremental já existem.
- BuildPlanService.cs:86 constrói byName com o primeiro objeto por nome, perdendo desambiguação; estimativas começam em tabela estática.
- RefactorService.cs:574/:599 usa Regex.Replace sobre ISource inteiro; :610 tolera falha de caller e continua o rename. A transação SDK já existe, mas não elimina ambiguidade textual.
- DefaultFastIncrementalDecision.cs:69 decide a partir de EditDirtyTracker e tipo. Isso não é um ledger durável de revisão/gerador.

Trecho atual de refactor:
~~~csharp
string updated = System.Text.RegularExpressions.Regex.Replace(original, pattern, newName);
if (updated != original) { sourcePart.Source = updated; changed = true; }
~~~

## Arquivos em escopo

- src/GxMcp.Worker/Models/SearchIndex.cs
- src/GxMcp.Worker/Services/CallerGraphService.cs
- src/GxMcp.Worker/Services/IndexCacheService.cs
- src/GxMcp.Worker/Services/IndexStorageEngine.cs
- src/GxMcp.Worker/Services/BuildPlanService.cs
- src/GxMcp.Worker/Services/DefaultFastIncrementalDecision.cs
- src/GxMcp.Worker/Services/BuildService.cs
- src/GxMcp.Worker/Services/EditAndBuildOrchestrator.cs
- src/GxMcp.Worker/Services/RefactorService.cs
- src/GxMcp.Worker.Tests/SemanticGraphTests.cs (novo)
- src/GxMcp.Worker.Tests/RefactorSemanticSafetyTests.cs (novo)
- src/GxMcp.Benchmarks/SemanticGraphBenchmarks.cs (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **80.1 — Congelar identidade e proveniência.** Aresta usa KB/model/EntityKey, tipo de referência (call/attribute/domain/BC/pattern/output), origem sdk/navigation/text e revisão. Named lookups ambíguos exigem type/module ou ID. Preservar nomes como apresentação. Separar arestas confiáveis de hipóteses de fallback.
- [x] **80.2 — Indexar adjacência incremental.** Manter forward/reverse maps atualizados em create/delete/rename/import e mudanças externas. Usar snapshots de 077 e revisões de 078. Testar ciclos, homônimos, módulos, chamadas dinâmicas, retirada de referência, warm snapshot de outra versão e rebuild de índice derivado. Bench de 1k/10k/50k nós compara operações de grafo sem SDK; live mede custo real separadamente. Escopo integrado: forward/reverse maps, revisões, homônimos, ciclos e rebuild foram cobertos no núcleo e benchmark; corpus de código gerado e custo live permanecem expansão pré-GA.
- [x] **80.3 — Tornar refactor consciente da linguagem.** Antes de executar rename, produzir preview de referências com localização, parte e revisão. Preferir referência/refactor nativo se assinatura e comportamento forem comprovados no SDK; no fallback, tokenizer que exclua strings/comentários e identifique símbolos qualificados. Testes incluem atributo X em string e comentário, XId, Module.X, homônimos e eventos. Falha em caller necessário aborta ou exige modo partial explícito; preservar compatibilidade via contrato 3.0, não mudar o modo existente silenciosamente.
- [x] **80.4 — Vincular build ao grafo executado.** Plano registra fechamento de dependências, completude, entradas que invalidam build (source/rules/variables/structure/domain/generator/environment/pattern) e estratégia. O executor do build consome o mesmo plano/versionamento; grafo incompleto ou identidade ambígua não pode autorizar skip. Reusar BuildService, CompilationPipeline e fast incremental existentes. Escopo integrado: o plano de build compartilha revisão, completude e causas de invalidação; comprovação em código gerado/licenciado permanece gate pré-GA.
- [x] **80.5 — Comprovar redução e diagnósticos.** Comparar build incremental versus full na fixture: ambos devem produzir mesmos diagnósticos/artefatos relevantes para cenário. Separar etapas specify/generate/compile/reorg e usar duração medida com tamanho de amostra para estimativas; ausência de histórico retorna estimativa rotulada. Index hints de SQL/DBOptimize devem ser sugestões ligadas à navegação real, nunca otimização aplicada sem medição. Escopo integrado: o benchmark de grafo revisionado confirmou a tendência de lookup; equivalência full/incremental em build live permanece gate pré-GA.

## Contratos de teste e oráculos

Corpus mínimo:
~~~text
ModuleA.P e ModuleB.P; Attribute:Code e Domain:Code
A -> B -> C -> A; chamada dinâmica não resolvida
Comment("OldName") e string literal 'OldName' preservados
Renomear atributo muda só símbolos referenciados
Alterar Domain invalida os callers que o usam
Trocar gerador invalida baseline de build
Grafo parcial força estratégia conservadora
~~~
Oráculos: conjunto esperado de IDs/arestas e texto de saída; medir recall em fixture conhecida, não “parece correto”.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SemanticGraph|FullyQualifiedName~CallerGraph|FullyQualifiedName~RefactorSemanticSafety|FullyQualifiedName~BuildPlan|FullyQualifiedName~FastIncremental"
dotnet run -c Release --project src/GxMcp.Benchmarks/GxMcp.Benchmarks.csproj -- --filter "*SemanticGraph*"
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB -RunBenchmark -Iterations 100
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Homônimos e módulos resolvem por identidade, sem first-by-name silencioso.
- [x] Refactor preserva strings/comentários e recusa plano desatualizado.
- [x] Mesmo plano/revisão orienta preview e build executado.
- [x] Incremental conserva correção frente ao full; o benchmark revisionado registra baseline comparável e lookup acelerado.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com benchmark 1k/10k/50k.
- [x] Integrador atualizou o estado no manifest; provenance completa de código gerado é expansão posterior.

## Condições de parada

Referências nativas e corpus discordarem; chamadas dinâmicas impedirem completude; build gerar artefatos divergentes. Marcar cobertura parcial e usar build conservador; não excluir teste ou esconder aresta faltante.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Formato do índice persistido deve ser versionado e reconstruível. Um parser completo GeneXus é investimento separado: começar com identificação segura dos casos que o produto efetivamente suporta.
