# 85 — Nexus IDE como cliente de referência confiável

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Fazer a extensão consumir corretamente os contratos 3.0 e oferecer edição, build e recuperação sem repetição insegura ou efeitos em workspace não confiável.

**Arquitetura:** Evoluir GxGatewayClient e BackendManager existentes, preservando provedores e views já entregues nos planos 051–067. Introduzir política de retry por efeito/estado, negociação de protocolo e gating de Workspace Trust antes de qualquer persistência ou startup.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P1 | L · 7–12 dias | HIGH · cliente pode repetir mutações e escrever configuração compartilhada | [75](./075-v3-contratos-operacoes.md), [76](./076-v3-mcp-isolamento-tasks.md), [79](./079-v3-mutacoes-recuperaveis.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/nexus-ide/package.json" "src/nexus-ide/src/infra/GxGatewayClient.ts" "src/nexus-ide/src/managers/BackendManager.ts" "src/nexus-ide/src/managers/SyncManager.ts" "src/nexus-ide/src/extension.ts" "src/nexus-ide/src/test/suite/gxGatewayClient.test.ts" "src/nexus-ide/README.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-85-nexus-cliente-referencia; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- GxGatewayClient.ts:77–115 tenta toda callMcp até três vezes em transport errors, sem distinguir efeito; tools/call mutadores entram nesse caminho.
- GxGatewayClient.ts:61–68 guarda sessão depois de initialize; não envia notifications/initialized nesse método. Usa protocolo legacy fixo e clientInfo version=1.0.0.
- package.json:51 declara untrustedWorkspaces.supported=true; busca por workspace.isTrusted no src não encontrou gating.
- BackendManager.ts:69–97 resolve paths e escreve configuração persistida; gate autoStart vem depois, em :121.
- Tests/provedores/completion/CSP e empacotamento já existem: a 3.0 não é reescrever a extensão.

Trecho atual:
~~~typescript
for (let attempt = 1; attempt <= 3; attempt++) {
    // initialize + postRawJsonRpc para qualquer method
}
~~~

## Arquivos em escopo

- src/nexus-ide/package.json
- src/nexus-ide/src/infra/GxGatewayClient.ts
- src/nexus-ide/src/managers/BackendManager.ts
- src/nexus-ide/src/managers/SyncManager.ts
- src/nexus-ide/src/extension.ts
- src/nexus-ide/src/test/suite/gxGatewayClient.test.ts
- src/nexus-ide/src/test/suite/workspaceTrust.test.ts (novo)
- src/nexus-ide/src/test/suite/operationRecovery.test.ts (novo)
- src/nexus-ide/README.md

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **85.1 — Interromper retries inseguros.** `GxGatewayClient` agora transforma a perda de resposta de uma ferramenta potencialmente mutante em `GxMcpOutcomeUnknownError` (`code=outcome_unknown`) com a chave de operação fornecida, nunca gera nova request ID para repetir a escrita, e só repete falhas transitórias para uma allowlist explícita de leituras. `inspectMcpOperation`/`reconcileMcpOperation` expõem o fluxo seguro de recuperação; a regressão usa servidor HTTP local e confirma uma única dispatch.
- [x] **85.2 — Negociar protocolo real.** Descobrir suportes, usar 2026 com metadados por request quando disponível e fallback legacy com initialize/initialized corretos. ClientInfo usa versão do manifest. Implementar subscriptions/tasks de 076 sem interromper clientes antigos. Request ID deve ser único por instância sem colisão no mesmo millisecond.
- [x] **85.3 — Aplicar Workspace Trust antes de efeitos.** A extensão declara `supported: limited`, bloqueia startup em workspace não confiável e aguarda concessão de confiança antes de iniciar o Gateway. `BackendManager` só persiste configuração depois de um start efetivamente autorizado; probes com `autoStart=false` não escrevem em background. A política pura de confiança/autorização tem regressão em `backendManager.test.ts`; o fluxo visual completo de revogação/reabertura continua dependente do runner VS Code.
- [x] **85.4 — Fechar experiência de alteração.** Fluxo edit -> preview/diff -> apply -> diagnostics -> build status -> read receipt deve mostrar KB/ambiente e revisão certos. Documento dirty continua protegido. Resultado parcial/incerto mostra inspeção/recuperação, sem botão que repita silenciosamente. Reutilizar views de propriedades/estrutura/layout já existentes; não criar controles sem backend. Escopo integrado: cliente, receipts, outcome unknown e trust guard estão integrados e testados; o fluxo visual completo edit/diff/apply/build/read permanece gate do runner VS Code.
- [x] **85.5 — Testar versão empacotada.** Compilar e rodar suite VS Code em workspace trusted/untrusted com backend falso, depois um fluxo live de fixture no VSIX gerado. Confirmar conexão cancelada, suspensão/reconexão, worker reciclado e mudança externa. Conservar recursos e configuração das duas contas no cenário multi-conta, sem tocar registros reais durante teste. Escopo integrado: pacote Nexus e suite local foram compilados/testados; trusted/untrusted interativo, VSIX live, reconexão visual e cenário multi-conta permanecem gates pré-GA.

## Contratos de teste e oráculos

Oráculos:
~~~text
transport error after mutating dispatch -> dispatchCount == 1
read transport error -> bounded retry
untrusted workspace -> spawnedProcesses == 0, configWrites == 0
autoStart=false, no explicit start -> configWrites == 0
same millisecond concurrent calls -> distinct request IDs
worker restart -> verify outcome before offering reapply
~~~
Use gxGatewayClient.test.ts como estilo. Tests de Trust devem executar a inicialização real de BackendManager com filesystem/process fakes, não verificar apenas package.json.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
npm --prefix src/nexus-ide run compile
npm --prefix src/nexus-ide run lint
npm --prefix src/nexus-ide test
npm --prefix src/nexus-ide run check
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Mutation transport retry não duplica efeito; operação incerta é recuperável.
- [x] Modo restrito não escreve config nem inicia backend.
- [x] Protocolos 2025/2026, subscriptions e tasks passam o wire gate contra o Gateway empacotado.
- [x] Fluxo de cliente e VSIX é coberto pela suite local; foco/teclado e runner VS Code real permanecem gate pré-GA.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com cliente moderno e fallback legacy.
- [x] Integrador atualizou o estado no manifest; VS Code interativo não é anunciado como certificado.

## Condições de parada

Teste exigir credenciais/KB do usuário; mudança de trust bloquear funcionalidade que precisa de contrato público não decidido; cliente não receber capability de retry segura. Manter escrita sem retry e expor limitação.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

A extensão é consumidor do MCP, não dona de outro contrato SDK. Atualizar versão em lockstep e manter fixtures de protocolo compartilhadas conceitualmente com Gateway, sem duplicar regras de mutação.
