# 76 — Conformidade MCP, isolamento de clientes e tasks

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Entregar cancelamento, progresso, subscriptions e operações demoradas com comportamento correto nos contratos MCP 2025-11-25 e 2026-07-28.

**Arquitetura:** Preservar o suporte dual já presente. Introduzir contexto explícito de request no Gateway, separar serialização por revisão do protocolo e adaptar os jobs existentes à extensão Tasks atual; nenhum estado de seleção deve vazar entre clientes HTTP sem sessão.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 10–15 dias | HIGH · concorrência, transporte e protocolo | [75](./075-v3-contratos-operacoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/Program.cs" "src/GxMcp.Gateway/Program.Http.cs" "src/GxMcp.Gateway/Program.RequestLoop.cs" "src/GxMcp.Gateway/Program.Notifications.cs" "src/GxMcp.Gateway/Program.WorkerLifecycle.cs" "src/GxMcp.Gateway/McpRouter.cs" "src/GxMcp.Gateway/McpHttpProtocol.cs" "src/GxMcp.Gateway/HttpSessionRegistry.cs" "src/GxMcp.Gateway/BackgroundJobRegistry.cs" "docs/technical_architecture.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-76-mcp-isolamento-tasks; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- McpRouter.cs:33 já declara ModernProtocolVersion=2026-07-28 e BuildServerDiscoverResponse em :344. McpHttpProtocol valida headers/body modernos.
- Program.cs:73 guarda McpRequestId no pending request, sem dono de sessão/transporte.
- Program.RequestLoop.cs:2723 compara cancelamento por McpRequestId textual em todos os pending.
- Program.WorkerLifecycle.cs:222–245 recebe progressToken do Worker e distribui frames às sessões HTTP ativas.
- BackgroundJobRegistry existe em memória. Não existem handlers subscriptions/listen ou tasks/get/update/cancel no Gateway auditado. Extensões são opcionais: isso é oportunidade, não violação por si só.

Trecho confirmado:
~~~csharp
if (!string.Equals(kvp.Value.McpRequestId, cancelled, StringComparison.Ordinal))
    continue;
~~~
A checagem não inclui cliente nem tipo original do ID JSON-RPC.

## Arquivos em escopo

- src/GxMcp.Gateway/Program.cs
- src/GxMcp.Gateway/Program.Http.cs
- src/GxMcp.Gateway/Program.RequestLoop.cs
- src/GxMcp.Gateway/Program.Notifications.cs
- src/GxMcp.Gateway/Program.WorkerLifecycle.cs
- src/GxMcp.Gateway/McpRouter.cs
- src/GxMcp.Gateway/McpHttpProtocol.cs
- src/GxMcp.Gateway/McpSubscriptionProtocol.cs (novo)
- src/GxMcp.Gateway/McpModernSubscriptionProtocol.cs (novo)
- src/GxMcp.Gateway/HttpSessionRegistry.cs
- src/GxMcp.Gateway/BackgroundJobRegistry.cs
- src/GxMcp.Gateway.Tests/RequestIsolationTests.cs (novo)
- src/GxMcp.Gateway.Tests/McpTasksContractTests.cs (novo)
- src/GxMcp.Gateway.Tests/McpSubscriptionContractTests.cs (novo)
- src/GxMcp.Gateway.Tests/McpModernSubscriptionContractTests.cs (novo)
- docs/technical_architecture.md

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **76.1 — Reproduzir isolamento.** No harness HTTP real sem SDK (worker simulado), abrir clientes A/B com requestId igual e progressTokens distintos. Cancelar A e demonstrar que B permanece pendente; testar IDs numérico 1 e string "1" separadamente. Incluir stdio simultâneo, sessão expirada, tarefa concluída e cancellation duplicado.
- [x] **76.2 — Carregar contexto ponta a ponta.** Cada pending agora preserva transporte/sessão, identidade JSON-RPC tipada, KB resolvida, ID interno e `progressToken` original. Frames do Worker continuam usando o ID interno apenas para correlação, são reescritos com o token do cliente e encaminhados somente ao stdio ou à sessão HTTP que iniciou a operação; requests modernos sessionless sem stream próprio são descartados com segurança. A regressão cobre tokens numérico/string, escopo de sessão e isolamento do transporte. `notifications/cancelled` moderno é aceito sem efeito, pois não há identidade de transporte para cancelar uma chamada anterior; o binding usa fechamento do response. O handle de task permanece separado de `requestId`.
- [x] **76.3 — Implementar subscriptions/listen.** O stream POST moderno agora aceita opt-ins por tipo/URI, envia `notifications/subscriptions/acknowledged` com um ID próprio, mantém fila e número de streams limitados, filtra cada evento por handle, inclui `kbAlias`/`cacheRevision`/`resourceUri` em atualizações de recursos, resolve leituras da URI qualificada para a KB explícita e limpa o estado ao desconectar. O caminho legacy continua com GET /mcp e `resources/subscribe` por sessão. Smoke wire com consumidor lento e reconexão foi executado e passou; eventos globais de catálogo e eventos privados de objetos devem continuar com políticas diferentes. Escopo integrado: subscriptions, ACK, filtro por handle e reconexão estão cobertos pelo wire conformance; eventos de produção específicos permanecem gate pré-GA.
- [x] **76.4 — Adaptar jobs à extensão atual.** Implementado `io.modelcontextprotocol/tasks` com `tasks/get`, `tasks/update` e `tasks/cancel` sobre o `BackgroundJobRegistry`, incluindo timestamps persistidos, ACKs `resultType=complete`, estados modernos, validação de `inputResponses` e isolamento por sessão. Operações assíncronas só retornam `resultType=task` quando a request 2026 declara a capacidade; lifecycle/status/result continuam compatíveis para clientes legados. A variante experimental `tasks/result/list` não é usada.
- [x] **76.5 — Fechar conformance dual.** A validação de headers, metadados, versão desconhecida, `resultType`, `cacheScope`/`ttlMs`, request sem id, limite explícito de corpo, matriz unitária de `Origin` e `Host` loopback contra DNS rebinding já está coberta em cortes anteriores; a distinção entre erros JSON-RPC e de ferramenta, desconexão e o smoke wire dual foi fechada. Não exigir auth remota num endpoint loopback por convenção; manter limites de confiança explícitos. Atualizar documentação que ainda afirma somente initialize + 2025. Escopo integrado: o conformance dual HTTP/stdio, slow consumer, reconexão, tasks fail-closed e validação Host/origin passou; carga longa com SDK real permanece gate pré-GA.

## Contratos de teste e oráculos

Oráculo obrigatório:
~~~json
{
  "clients": [
    {"client": "A", "id": 1, "progressToken": "pa"},
    {"client": "B", "id": 1, "progressToken": "pb"}
  ],
  "cancel": "A",
  "expected": {
    "A": "cancelled_or_outcome_unknown",
    "B": "still_running",
    "crossClientNotifications": 0
  }
}
~~~
A combinação cancel_requested/operation-outcome desconhecido nunca deve afirmar rollback. Testar HTTP e stdio reais, não apenas chamadas ao Router. Para Tasks, ausência da capacidade deve preservar resultado síncrono/lifecycle compatível.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~RequestIsolation|FullyQualifiedName~McpTasksContract|FullyQualifiedName~McpSubscriptionContract|FullyQualifiedName~McpHttpProtocol|FullyQualifiedName~McpHandshake"
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
pwsh -NoProfile -File scripts/mcp_llm_contract_smoke.ps1
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Cancelamento de A nunca resolve/aborta chamada de B com mesmo ID; a matriz de sessão/tipo passa.
- [x] Zero frames de progresso com token incorreto ou enviados a cliente não relacionado.
- [x] Tasks/subscriptions têm testes unitários e wire de capacidade, ACK, consumidor lento e reconexão.
- [x] Contrato legado continua passando; cliente sem sessão usa KB explícita ou fallback persistido, sem seleção compartilhada.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com smoke dual reproduzível.
- [x] Integrador atualizou o estado no manifest; desconexão moderna sem identidade própria permanece fail-closed.

## Condições de parada

SDK não conseguir cancelar uma fase: retornar estado honesto e aguardar/reconciliar; não matar um Worker com mutação em andamento só para satisfazer deadline. Capacidade Tasks que não tenha implementação verificada não deve ser anunciada.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Consultar a especificação pinada 2026-07-28, não o endereço latest durante execução. Recursos privados e handles devem ter escopo verificável; um UUID não substitui autorização quando houver identidade autenticada.
