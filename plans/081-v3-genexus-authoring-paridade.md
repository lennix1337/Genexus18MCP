# 81 — Authoring GeneXus orientado a capacidades e paridade

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Permitir que agentes saibam o que realmente funciona na instalação GeneXus/WWP e executem authoring com valores válidos e persistência comprovada.

**Arquitetura:** Evoluir probes e adapters existentes para matriz de capacidades por versão SDK/WWP, gerador e tipo de objeto. Entregar cortes verticais de propriedades, patterns e traduções somente depois de provar construção dos argumentos, persistência após reabertura e equivalência do resultado.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P1 | L · 10–20 dias; traduções/WWP avançado dependem de spike | HIGH · APIs opcionais e persistência em partes heterogêneas | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md), [77](./077-v3-executor-sdk.md), [79](./079-v3-mutacoes-recuperaveis.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Worker/Services/SdkSurfaceProbe.cs" "src/GxMcp.Worker/Services/SdkProbeService.cs" "src/GxMcp.Worker/Helpers/SdkServiceResolver.cs" "src/GxMcp.Worker/Services/PropertyService.cs" "src/GxMcp.Worker/Services/PatternEngineAdapter.cs" "src/GxMcp.Worker/Services/PatternApplyService.cs" "src/GxMcp.Worker/Services/PatternParityHarness.cs" "src/GxMcp.Worker/Services/TranslationsService.cs" "src/GxMcp.Worker/Services/LayoutService.VisualContext.cs" "src/GxMcp.Worker.Tests/TranslationsServiceTests.cs" "docs/sdk_coverage_gap_matrix.md" "docs/sdk_endpoints_roadmap.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-81-genexus-authoring-paridade; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- PatternEngineAdapter.cs:45 usa reflexão para carregar package opcional e binder de overloads; isso é necessário em parte, não dívida a eliminar cegamente.
- TranslationsService.cs:57–79 parseia CSV, devolve ItemDeferred e updated=0; escrita por idioma permanece não conectada.
- PatternParityHarnessTests.cs:126 ainda não carrega KB para sua comparação live.
- docs/sdk_coverage_gap_matrix.md:3 avisa que várias lacunas antigas já foram entregues. TransferService, DeployService, CiPipelineService, SecurityScanService e TableRelationsService existem e não devem ser reimplementados.
- docs/sdk_coverage_gap_matrix.md:115 registra obstáculo real em persistência WWP Settings/Components; não assumir que mais chamadas por reflexão o resolvem.

Trecho atual:
~~~csharp
result["status"] = "Unwired";
result["code"] = "ItemDeferred";
result["updated"] = updated;
~~~

## Arquivos em escopo

- src/GxMcp.Worker/Services/SdkSurfaceProbe.cs
- src/GxMcp.Worker/Services/SdkProbeService.cs
- src/GxMcp.Worker/Helpers/SdkServiceResolver.cs
- src/GxMcp.Worker/Services/PropertyService.cs
- src/GxMcp.Worker/Services/PatternEngineAdapter.cs
- src/GxMcp.Worker/Services/PatternApplyService.cs
- src/GxMcp.Worker/Services/PatternParityHarness.cs
- src/GxMcp.Worker/Services/TranslationsService.cs
- src/GxMcp.Worker/Services/LayoutService.VisualContext.cs
- src/GxMcp.Worker.Tests/SdkCapabilityContractTests.cs (novo)
- src/GxMcp.Worker.Tests/TranslationsServiceTests.cs
- docs/sdk_coverage_gap_matrix.md
- docs/sdk_endpoints_roadmap.md

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **81.1 — Publicar matriz honesta.** Capability status distingue supported_verified, available_unverified, unavailable, unsupported e deferred; incluir versão instalada, adapter, tipos cobertos e último teste da fixture, sem dizer que assinatura encontrada prova funcionamento. Catálogo MCP moderno não varia por sessão: a disponibilidade de uma KB é resultado de consulta explícita. Reutilizar sdk_probe/doctor e schema de 075. Implementado como `genexus_sdk_probe { mode: "capabilities" }`; status de persistência permanece falso até a fixture certificada.
- [x] **81.2 — Provar propriedades por resolver.** Spike read-only para um Attribute, Transaction, SDT e WebPanel: obter tipo/valores válidos/readonly/visible pelo resolver SDK comprovado. Se o resolver exigir shell IDE, declarar unavailable e mostrar metadados seguros. Só depois ligar validação pré-write em PropertyService; valor inválido deve produzir zero saves. Escopo integrado: o resolver e o guard de propriedades falham fechado sem superfície comprovada; a prova read-only de Attribute/Transaction/SDT/WebPanel depende das superfícies SDK disponíveis e permanece gate pré-GA.
- [x] **81.3 — Certificar patterns.** Na fixture, capturar baseline IDE conhecida e repetir apply/reapply pelo adapter existente; comparar família gerada, variáveis, Rules, Events, WebForm e parte de pattern depois de restart. Cobrir WWP instalado/ausente, versão não certificada, customizações preservadas e reapply sem mudança. Investigar sequência de hooks apenas onde a diferença reproduzida a exigir; não disparar hooks em duplicidade com PatternEngine. Escopo integrado: capability probing e adapter fail-closed estão publicados; baseline IDE, reapply e parity WWP permanecem gate pré-GA por ausência da instalação opcional.
- [x] **81.4 — Entregar traduções por corte vertical.** Primeiro spike de leitura e roundtrip em um caption WebPanel de dois idiomas. Documentar qual objeto/parte é autoritativo e então implementar validate/preview/apply com receipt 079. Estender Transaction e Menu separadamente. CSV inclui objeto desambiguado, idioma e propriedade permitida; erro de uma linha não vira updated. Testar acentos, aspas, linha ausente e releitura após reinício. Escopo integrado: a fronteira de capabilities e contratos de erro está integrada; o roundtrip de tradução em dois idiomas permanece gate pré-GA por falta de fixture/capability certificada.
- [x] **81.5 — Fechar documentação e opções avançadas.** Atualizar matrizes antigas com link a handler/teste e status atual. DSO tokens/classes, SDPanel e WWP settings/componentes ficam em fila de spikes limitados: ler estrutura real, provar um roundtrip, decidir viabilidade. O caso bloqueado de WWP não autoriza XML global especulativo. Selecionar no máximo uma expansão por evidência de uso antes do RC. Escopo integrado: matrizes, documentação e fila de spikes estão atualizadas; DSO/SDPanel/WWP avançado permanece deliberadamente deferred até evidência autorizada.

## Contratos de teste e oráculos

Formato de capability proposto:
~~~json
{
  "capability": "translations.webpanel.caption",
  "status": "available_unverified",
  "scope": {"objectType": "WebPanel"},
  "evidence": {"kind": "signature_probe", "persistenceVerified": false}
}
~~~
Teste do catálogo deve rejeitar status supported_verified quando só houve probe de assinatura. Testes live reabrem KB; o teste unitário do parser CSV continua existindo e não conta como persistência.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SdkCapabilityContract|FullyQualifiedName~PatternApply|FullyQualifiedName~PatternParity|FullyQualifiedName~Translations"
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpDiscoveryContract"
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Capability diferencia presença de assinatura e persistência certificada.
- [x] Valores inválidos de propriedade são rejeitados antes de salvar.
- [x] Pattern reapply usa harness seguro e reporta explicitamente a ausência de WWP/IDE baseline; paridade completa é gate pré-GA.
- [x] Tradução só contabiliza updated depois de prova persistida; tipos não implementados permanecem `deferred`/`unwired` explícitos.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com matriz de capability e guard de propriedade.
- [x] Integrador atualizou o estado no manifest; não há capability WWP ou tradução não certificada anunciada.

## Condições de parada

Resolver exige UI; WWP devolve view que não persiste; baseline IDE não disponível; API restrita por licença ou obfuscação. Encerrar spike com evidência e capability unavailable/deferred. Não fabricar equivalência nem recorrer a mutação global de XML.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Cada combinação SDK Update/WWP/gerador certificada deve apontar ao corpus. Nova versão não herda status verified sem teste. Reconciliar 050: tradução ainda pendente; o estado de DSO exige seu próprio spike.
