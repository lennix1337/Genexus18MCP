# 78 — Cache coerente por revisão de KB e dependências

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Garantir que leituras, listas e análises reflitam mudanças confirmadas, inclusive alterações externas, sem jogar fora todo o cache de outras KBs.

**Arquitetura:** Substituir invalidação por substring por chaves estruturadas e gerações de KB/modelo/ambiente/objeto/coleção. Começar invalidando conservadoramente a KB afetada, depois refinar tags com evidência; usar tempo absoluto de validade além de LRU.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 5–8 dias | MED · frescor versus hit rate | [75](./075-v3-contratos-operacoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/SemanticCacheStore.cs" "src/GxMcp.Gateway/Program.ToolPayload.cs" "src/GxMcp.Gateway/Program.RequestLoop.cs" "src/GxMcp.Gateway/Program.WorkerLifecycle.cs" "src/GxMcp.Gateway/Program.KbContext.cs" "src/GxMcp.Gateway.Tests/SemanticCacheInvalidationTests.cs" "src/GxMcp.Gateway.Tests/SemanticCacheGranularInvalidationTests.cs"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-78-cache-revisoes; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- SemanticCacheStore.cs mantém `createdAt` absoluto separado de `lastAccess`, revisões por escopo e um relógio monotônico injetável para testes.
- Program.RequestLoop.cs invalida a geração inteira da KB em mutação persistente; previews/validate-only preservam a geração e KBs não afetadas permanecem quentes.
- A chave de dispatch canonicaliza objetos JSON e inclui `rev`, além de identidade de model/environment quando o handle já tem um snapshot fresco.
- SemanticCacheEpoch continua como fence global para reinício/alteração externa; a revisão por KB evita bloquear ou repovoar indevidamente outras KBs. `records_*` continua bypassando o cache.

Trecho histórico que motivou o corte:
~~~csharp
string needle = "\"" + targetObject + "\"";
if (argsJson.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
    && RemoveEntry(key))
~~~
Uma lista com args {limit:10}, aquecida antes de criar um objeto nomeado, não contém o nome para ser removida.

## Arquivos em escopo

- src/GxMcp.Gateway/SemanticCacheStore.cs
- src/GxMcp.Gateway/Program.ToolPayload.cs
- src/GxMcp.Gateway/Program.RequestLoop.cs
- src/GxMcp.Gateway/Program.WorkerLifecycle.cs
- src/GxMcp.Gateway/Program.KbContext.cs
- src/GxMcp.Gateway.Tests/SemanticCacheInvalidationTests.cs
- src/GxMcp.Gateway.Tests/SemanticCacheGranularInvalidationTests.cs
- src/GxMcp.Gateway.Tests/CacheRevisionTests.cs (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **78.1 — Reproduzir com população real.** Adicionar testes de list/query vazia antes de create; delete após list; rename afetando dependentes; Property/Structure mudando filtro; ambiente alterado com mesmo alias; KB B preservada. Testar a sequência pelo pipeline de RequestLoop, não só o store.
- [x] **78.2 — Definir identidade e relógio.** Chave de cache do dispatch contém KB canônica, argumentos JSON canonicalizados, geração da KB e a identidade de model/environment quando o handle já possui esse snapshot. A revisão de geração é separada da recência LRU. O store aceita relógio monotônico injetável em teste; um hit atualiza apenas a recência e nunca estende o `createdAt + maxAge` absoluto. Hashes de conteúdo continuam sem valor de autorização.
- [x] **78.3 — Trocar invalidação.** Mutações confirmadas ou de resultado incerto agora invalidam a geração inteira da KB tocada, preservando entradas de outras KBs. A fronteira de dispatch captura a geração e impede que uma leitura anterior a uma mutação repovoe a geração nova. Preview/validate-only não apaga cache válido. Refinamento por tags permanece posterior e depende do contrato 075.
- [x] **78.4 — Integrar eventos externos.** Notification do watcher deve carregar KB/model e identidade estável; invalidar a geração apropriada antes de publicar resource updated. Troca de ambiente, close/reopen, import, GXserver update, restore e worker recycle invalidam dados relacionados. Em falta de identidade, invalidar conservadoramente somente a KB de origem. Escopo integrado: invalidação por geração, ciclo de Worker e eventos identificados estão cobertos; import/restore/GXserver e produtores externos exigem fixtures independentes pré-GA.
- [x] **78.5 — Medir o custo correto.** Comparar warm read e read-after-write com baseline 074. A redução de hit rate que elimina resposta stale é correção, não regressão aceitável de frescor. Demonstrar manutenção dos hits de KB B e, após tags verificadas, objeto não relacionado na mesma KB. Escopo integrado: o benchmark warm e read-after-write disponível foi executado; comparação de duas KBs e tags de objeto não relacionado permanecem gates pré-GA.

## Contratos de teste e oráculos

Tabela que deve virar teoria xUnit:
~~~text
warm list(empty) -> create X -> list                 includes X
warm query(type=Procedure) -> delete X -> query       excludes X
warm inspect(caller Y) -> rename callee X -> inspect  fresh
read in flight -> mutation -> read completes         no old generation cached
hot hit loop -> advance absolute maxAge -> get        miss
mutate KB A -> read KB B                              cache hit remains valid
~~~
Usar SemanticCacheInvalidationTests/Idempotency test style existente. Acrescentar testes de timestamp monotônico separado de relógio TTL.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~SemanticCache|FullyQualifiedName~CacheRevision|FullyQualifiedName~GranularCache"
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Sequências de invalidação e read-after-write do núcleo retornam a população correta; restore/import multi-KB é gate pré-GA.
- [x] Nenhuma entrada vive indefinidamente apenas por receber hits.
- [x] Escopo inclui modelo/ambiente e a geração de uma KB não invalida outra.
- [x] Teste de read em andamento não permite repovoar geração obsoleta.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com revisão, TTL e geração.
- [x] Integrador atualizou o estado no manifest; eventos externos sem identidade usam invalidação conservadora.

## Condições de parada

Evento do Worker não permite identificar KB/model com segurança; novo predicado de query não mapeável a tags. Usar invalidação da KB afetada até haver identidade/predicado explícito; não conservar entrada ambígua.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Qualquer campo que mude uma população de query deve declarar sua dependência. Contadores/listas/paginação compartilham o mesmo predicado e revisão.
