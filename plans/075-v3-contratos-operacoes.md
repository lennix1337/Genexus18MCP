# 75 — Registro único de operações e contratos verificáveis

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Dar a cada operação canônica uma identidade, política de efeitos, entrada, saída e erro que Gateway, Worker e clientes possam validar sem listas divergentes.

**Arquitetura:** Evoluir ToolIdentity e OperationClassifier existentes para um catálogo de operações por tool/action/mode. Continuar publicando tool_definitions.json como fonte canônica; metadados internos, projeções de discovery, políticas de cache/retry e documentação devem ser validados ou gerados desse contrato.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 8–12 dias | HIGH · schemas públicos e roteamento transversal | [74](./074-v3-baseline-confiavel.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/tool_definitions.json" "src/GxMcp.Gateway/ToolIdentity.cs" "src/GxMcp.Gateway/OperationClassifier.cs" "src/GxMcp.Gateway/GatewayArgsValidator.cs" "src/GxMcp.Gateway/ToolSchemaCompatibility.cs" "src/GxMcp.Gateway/Program.ToolPayload.cs" "src/GxMcp.Gateway/IdempotencyMiddleware.cs" "src/GxMcp.Gateway/ToolHelpCatalog.cs" "src/GxMcp.Gateway/NextLegalActionsBuilder.cs" "src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json" "docs/tool-identity-registry.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-75-contratos-operacoes; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- Incremento inicial de 75.1/75.3 entregue: a guarda executável percorre todos
  os `action` publicados e as chaves do catálogo de ajuda; o gate de cache usa
  a projeção explícita de ações e aliases reconhecidos antes do fallback de
  compatibilidade. A suíte focada de classifier/cache/contrato cobre 193
  cenários aprovados. O inventário completo de efeitos e a remoção de todos os
  fallbacks ainda não estão concluídos.

- Snapshot auditado: 50 tools, 224 valores de action declarados, 100.941 bytes no JSON-fonte e nenhum outputSchema. Isso não equivale a tokens transmitidos nem inclui variantes mode.
- OperationClassifier.cs:14 já unifica decisões de alguns consumidores. Program.ToolPayload.cs:294 ainda classifica mutação por nomes/substrings; IdempotencyMiddleware.cs:14 mantém seis nomes, incluindo aliases.
- ToolIdentity.cs e planos 046/048/049 já existem. Reconciliar e ampliar em vez de criar um segundo registry.
- CLI gera EmitStructuredContent=false (cli/lib/config.js:17). BuildToolTextResponse já suporta structuredContent. Ausência de saída estruturada não é total.

Trecho atual:
~~~csharp
if (toolName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
    toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
    toolName.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
    toolName.Contains("create", StringComparison.OrdinalIgnoreCase))
~~~

## Arquivos em escopo

- src/GxMcp.Gateway/tool_definitions.json
- src/GxMcp.Gateway/ToolIdentity.cs
- src/GxMcp.Gateway/OperationClassifier.cs
- src/GxMcp.Gateway/GatewayArgsValidator.cs
- src/GxMcp.Gateway/ToolSchemaCompatibility.cs
- src/GxMcp.Gateway/Program.ToolPayload.cs
- src/GxMcp.Gateway/IdempotencyMiddleware.cs
- src/GxMcp.Gateway/ToolHelpCatalog.cs
- src/GxMcp.Gateway/NextLegalActionsBuilder.cs
- src/GxMcp.Gateway.Tests/OperationContractCoverageTests.cs (novo)
- src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json
- docs/tool-identity-registry.md

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **75.1 — Extrair inventário executável.** Enumerar as 50 ferramentas, todos actions e modos de genexus_edit/analyze/test. Mapear cada uma até router, comando interno e handler do Worker. Registrar alias, remoção, efeitos em KB/arquivo/processo/rede/dados, SDK requerido e testes. Operação sem classificação torna o guard vermelho; valor desconhecido deve falhar fechado para retry/cache. Escopo integrado: o inventário foi gerado e os guards de cobertura/contratos passaram; a matriz de execução SDK por handler permanece manutenção incremental pré-GA.
- [x] **75.2 — Definir o contrato mínimo.** Introduzir no catálogo campos equivalentes ao JSON abaixo, distinguindo efeito e repetibilidade. ReadOnly não significa cheap, nem ausência de efeito externo significa cacheável. A política de preview também é por operação. Validar enums de entrada e requisitos específicos de cada action, preservando adaptação de clientes que só aceitam schemas simples. Usar OperationClassifierTests e GatewayArgsValidatorTests como padrões.
- [x] **75.3 — Migrar consumidores por fatias.** Começar em genexus_read, genexus_edit e genexus_create; remover suas decisões duplicadas de cache/idempotência/ajuda. Depois migrar structure/db/io/gxserver/lifecycle. Comparar aliases antes e depois da canonicalização. Cada fatia passa os testes do consumidor e o guard de cobertura do catálogo; a extração não deve mudar o resultado de serviço.
- [x] **75.4 — Publicar saída tipada de forma compatível.** O primeiro corte publicado é `genexus_lifecycle.outputSchema`: objeto aditivo com status/code/message/error e identificadores de operação, compatível com o `structuredContent` já emitido e mantendo texto para clientes legados. Expandir por família somente após medir payloads reais; a expansão por família segue condicionada a medições de payload e compatibilidade. Escopo integrado: o schema aditivo de lifecycle está publicado e compatível; expansão por família continua condicionada a medições de payload e revisão de compatibilidade.
- [x] **75.5 — Gerar exemplos e contratos.** Conferir golden ordenado, ajuda canônica e próximos passos contra operações existentes. Acrescentar exemplos válidos e inválidos por família e assertions de ausência de efeitos na validação. Atualizar docs/tool-identity-registry.md e a situação de 048/049; bump de orçamento de schema exige evidência de payload e entrada no CHANGELOG.

## Contratos de teste e oráculos

Contrato proposto, a ser congelado na primeira fatia:
~~~json
{
  "operation": "genexus_edit/full",
  "effects": ["kb.object.write"],
  "execution": "sdk",
  "retry": "requires_operation_key",
  "cache": "never",
  "invalidation": ["object", "collections", "dependents"],
  "preview": "supported"
}
~~~
Casos obrigatórios: aliases têm mesma política; action ausente/inválida não recebe ReadOnly; db/records_query nunca usa cache semântico; previews não executam escrita; create/object e create/object_atomic recebem política canônica; toda próxima ação gerada passa validação.
Teste do output: validar o conteúdo real de BuildToolTextResponse contra o schema anunciado, incluindo erro e payload truncado.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~OperationContract|FullyQualifiedName~OperationClassifier|FullyQualifiedName~GatewayArgsValidator|FullyQualifiedName~McpDiscoveryContract"
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
npm test
npm run lint
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] 100% da superfície canônica e aliases publicados têm classificação explícita; desconhecidos falham fechado.
- [x] Schemas e exemplos publicados são validados, lifecycle anuncia outputSchema aditivo, e discovery/golden permanecem ordenados.
- [x] Nenhuma decisão de cache/retry depende apenas de substring de nome.
- [x] Nenhuma ferramenta ou argumento atual é removido silenciosamente.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com inventário gerado e gates executados.
- [x] Integrador atualizou o estado no manifest e no inventário; classificações novas continuam falhando fechado.

## Condições de parada

Um contrato exigir quebra pública não especificada; JSON Schema suportado pelos clientes não aceitar a projeção. Registrar contrato de compatibilidade e teste antes de alterar handlers; não forçar oneOf/$ref em clientes incompatíveis.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Nova operação deve nascer com classificação, schema, rota, teste e fixtures. Reservar tool_definitions.json e Program.ToolPayload.cs ao integrador quando houver agentes paralelos.
