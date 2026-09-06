# 82 — Contexto eficiente, observabilidade e avaliação de agentes

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Reduzir chamadas e tokens por tarefa bem-sucedida, com rastreamento que identifique custo de fila, SDK, I/O e payload sem expor conteúdo de KB.

**Arquitetura:** Aproveitar resources/prompts, perfis de tools, projeções e métricas existentes. Criar pacotes de contexto endereçáveis por KB/revisão e avaliações de fluxos reais; desempenho de agente é medido por sucesso, ferramentas corretas, retries e custo, não apenas duração média de RPC.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P1 | L · 6–10 dias | MED · contexto desatualizado, payload e privacidade | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md), [76](./076-v3-mcp-isolamento-tasks.md), [78](./078-v3-cache-revisoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/ToolProfileFilter.cs" "src/GxMcp.Gateway/McpRouter.cs" "src/GxMcp.Gateway/ToolLatencyStats.cs" "src/GxMcp.Gateway/OperationTracker.cs" "src/GxMcp.Gateway/Program.ToolPayload.cs" "src/GxMcp.Gateway/ResponseSizeGuard.cs" "src/GxMcp.Worker/Services/OrientService.cs" "docs/environment_variables.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-82-contexto-observabilidade-avaliacoes; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- ToolProfileFilter.cs:10 já oferece core/authoring/devops/ui/db; não criar perfis redundantes.
- CLI config.js:17 gera EmitStructuredContent=false e TerseResponses=true; existem fields/projection/full e ResponseSizeGuard.
- ToolLatencyStats.cs:9 documenta agregado por tool que exclui cold-start; agrupar genexus_db inteiro mistura actions de custos distintos.
- Resources/prompts/orient/read targets já existem; evolução útil é seleção consistente de contexto e redução mensurável de roundtrips.
- 50 tools/224 actions tornam precisão de escolha tão importante quanto tamanho do catálogo; nenhum número de tokens foi medido nesta auditoria.

## Arquivos em escopo

- src/GxMcp.Gateway/ToolProfileFilter.cs
- src/GxMcp.Gateway/McpRouter.cs
- src/GxMcp.Gateway/ToolLatencyStats.cs
- src/GxMcp.Gateway/OperationTracker.cs
- src/GxMcp.Gateway/Program.ToolPayload.cs
- src/GxMcp.Gateway/ResponseSizeGuard.cs
- src/GxMcp.Worker/Services/OrientService.cs
- src/GxMcp.Worker/Services/ContextBundleService.cs (novo)
- src/GxMcp.Gateway.Tests/ContextBundleContractTests.cs (novo)
- plans/v3-evaluation-corpus.json (desenho nesta entrega; implementação futura em tests/agent-evals/corpus.json)
- docs/environment_variables.md
- scripts/validate-agent-replay.py (novo)
- scripts/tests/test_validate_agent_replay.py (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **82.1 — Instrumentar o caminho completo.** Correlation/operation ID atravessa Gateway → Worker; o envelope de telemetria separa startup, espera efetiva na fila (`_meta.queuedAtUtc`), SDK, transformação, serialização, bytes de resposta, cache e classe de resultado. Timestamps inválidos/futuros falham fechado e não alteram a execução; agregação permanece sem nomes de objetos, paths de KB ou request IDs.
- [x] **82.2 — Definir corpus de tarefas.** Materializado em `tests/agent-evals/corpus.json` com fixture sintética revisionada, oráculos determinísticos e modo `deterministic-replay`; o contrato mantém `modelEvaluation=not_executed` até existir um replay autorizado. O validador e a documentação não tratam a especificação como evidência de execução.
- [x] **82.3 — Criar pacote de contexto limitado.** Corte inicial: explicar/editar uma Procedure. Uma leitura retorna assinatura, partes selecionadas, variáveis, referências diretas, revisão e diagnósticos relevantes; conteúdo grande vira recurso endereçável com paginação, hash e next cursor. Budget truncado deve preservar contexto sintaticamente útil e marcar omissões, nunca cortar JSON. Resolução não autoriza leitura de outras KBs. Implementado no `ContextBundleService` sobre `genexus_analyze` `mode=context`, com `maxBytes`, `cursor`, hash SHA-256 e referências `genexus_read`.
- [x] **82.4 — Otimizar a descoberta com evidência.** Comparar perfis fixos já existentes, descrições e exemplos gerados por 075. `tools/list` moderno permanece determinístico e independente de conexão. O recurso `genexus://kb/capabilities` e o primeiro `genexus_lifecycle.outputSchema` já estão publicados; ampliar schemas estruturados somente com medições de payload e compatibilidade. Preservar cliente textual, medindo duplicação real antes de escolher default.
- [x] **82.5 — Criar gates de agente e observabilidade.** Tarefa que escolhe ferramenta errada, modifica objeto além do escopo ou ignora outcome unknown falha mesmo se rápida. Baseline deve comparar o mesmo corpus, modelo e revisão. O gate determinístico foi materializado em `scripts/validate-agent-replay.py` e cobre E01–E15, revisão da fixture, chamadas inválidas, efeitos indevidos, retries cegos e vazamento de source/segredos; a execução com modelo/KB permanece não executada até existir fixture autorizada e é gate pré-GA. Testar ausência de source/credenciais em logs e tratar conteúdo de KB como dados, não instruções em prompts. Escopo integrado: o gate determinístico E01–E15 e a política de logs foram executados; replay com modelo e medição de tokens permanece explicitamente não executado e é gate pré-GA.

## Contratos de teste e oráculos

Métricas propostas:
~~~json
{
  "operation": "genexus_read",
  "resultClass": "success",
  "queueWaitMs": 0,
  "sdkMs": 0,
  "responseBytes": 0,
  "cacheOutcome": "miss",
  "taskMetrics": ["success", "toolCalls", "invalidCalls", "tokens"]
}
~~~
Zeros são somente forma do contrato, não medições. Casos: resource expirado; KB errada; budget pequeno; texto hostil na descrição de objeto; cliente textual; saída estruturada; mesma tarefa em cache frio/quente.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ContextBundleContract|FullyQualifiedName~ToolLatency|FullyQualifiedName~ResponseSize|FullyQualifiedName~ToolProfile"
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
pwsh -NoProfile -File scripts/mcp_llm_contract_smoke.ps1
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Fluxo medido separa cold-start/fila/SDK/payload e erro de sucesso.
- [x] Context bundle retorna revisão, escopo, omissões e recursos pagináveis válidos.
- [x] Corpus tem oráculos de resultado e de ausência de efeitos indevidos.
- [x] Ganho de tokens/chamadas só é publicado quando aumenta ou conserva sucesso do mesmo corpus; replay com modelo permanece não executado.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com corpus determinístico de 15 cenários.
- [x] Integrador atualizou o estado no manifest; avaliação model-backed continua explicitamente `not_executed`.

## Condições de parada

Dados de KB precisariam ser enviados a provedor externo não autorizado; revisão de recurso não pode ser conferida; avaliação requer modelo sem orçamento. Continuar replay determinístico e registrar etapa com modelo como não executada.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Prompts/resources são contratos do produto e devem ter exemplos executáveis. Evitar tornar cada oportunidade de SDK uma nova ferramenta: avaliar reutilização de famílias já descobertas.
