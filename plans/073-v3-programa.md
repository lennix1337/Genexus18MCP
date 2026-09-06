# Programa 3.0 — GeneXus MCP

**Data:** 2026-09-05 · **Base:** 2.57.0 · **Commit:** b3d20f731d47d2e0ae20c179ac0776a047763ade
**Entrega:** programa integrado no núcleo 3.0; implementação, contratos,
artefato candidato e gates disponíveis foram revisados. Nenhuma publicação foi
realizada.

A 3.0 deve transformar o MCP numa interface de engenharia GeneXus confiável para vários agentes: cada ação tem identidade e efeitos definidos; cada escrita tem resultado verificável; cada decisão de impacto/build tem revisão e evidência; cada ganho de desempenho conserva a correção.

A recomendação é evoluir a arquitetura de dois processos. A separação Gateway/Worker protege a compatibilidade do SDK. Reescrever tudo, migrar o Worker para .NET moderno ou aumentar indiscriminadamente o catálogo desviaria esforço das limitações observadas.

## Leitura orientada à execução

1. O mantenedor/integrador lê este programa.
2. Cada executor recebe **um** pacote 074–085 completo, sem depender desta conversa.
3. 086 integra e certifica a candidata; não publica automaticamente.
4. [Manifest de execução](./v3-execution.json) contém dependências, escopos e status. [Corpus inicial](./v3-evaluation-corpus.json) define cenários e oráculos para 074/082.
5. O estado atual dos pacotes está no manifest. `VERIFIED_INTEGRATED` significa
   núcleo integrado com evidência executável; WWP, multi-KB, replay model-backed,
   runner VS Code interativo e soak prolongado continuam gates pré-GA explícitos.

## O que já existe e deve ser preservado

O snapshot integrado tem 50 ferramentas canônicas, 207 valores de action no
schema e 226 operações no inventário projetado. Esses números são estáticos: o
payload publicado muda com projeção/perfil, e bytes não são uma contagem de
tokens. Há ainda variantes por mode, comandos internos e aliases.

Inventário de arquivos C#/TS rastreados: Gateway 65, Worker 292, testes Gateway 135, testes Worker 292, benchmarks 9 e Nexus IDE 60. Quantidade de arquivos não mede cobertura nem qualidade.

A base já inclui:

- MCP stdio/Streamable HTTP; sessões legacy e suporte inicial a 2026-07-28, server/discover e metadados modernos.
- Seleção de KB por contexto, pool/supervisor, jobs/status, cancellation registry, watchdog/recycle.
- Índices secundários e incrementais, snapshots warm, cache limitado, compactação/projeção, perfis de ferramentas.
- SDK authoring, atomic create, snapshots, dry-run, baseVersion, verificação pós-save e receipts de patch.
- Transferência XPZ nativa, comparação/merge de objetos, GXserver/pipelines, deploy, segurança nativa, relações de tabelas, geração de referência e dados tipados.
- CLI com zero dependências de runtime declaradas, adapters de clientes, diagnósticos, pacote npm e extensão VS Code com testes/compilação/lint.
- CI com coverage floors, smoke e guards adicionados no próprio HEAD. Não existe justificativa para planejar “criar testes/CI do zero”.

## Achados confirmados por leitura do caminho real

**Fato** significa código/contrato observado, não incidente reproduzido numa KB nesta sessão. **Hipótese** descreve consequência que precisa de teste discriminante. Esforço S/M/L é relativo à correção isolada; os pacotes incluem mudanças maiores. Confiança alta se refere ao padrão encontrado. P0 indica que a 3.0 não pode certificar o produto com o problema em aberto.

| ID / prioridade | Fato e consequência a verificar | Evidência no snapshot | Confiança | Esforço / risco da correção | Pacote |
|---|---|---|---|---|---|
| A01 P0 | Invalidação por substring do alvo não remove list/query sem aquele nome nos argumentos; TTL renova a cada hit. Leituras coletivas podem permanecer stale. | SemanticCacheStore.cs:57,97; Program.RequestLoop.cs:1589 | Alta | M / MED | 078 |
| A02 P0 | Após 30s sem adquirir gate, idempotency executa factory novamente. Uma escrita lenta pode executar duas vezes. | IdempotencyCache.cs:54–72 | Alta | M / HIGH | 079 |
| A03 P0 | Rollback de unidade composta ignora retorno/falha por alvo e marca rolledBack=true. O receipt pode afirmar restauração não provada. | MutationEngine.cs:497–509 | Alta | M / HIGH | 079 |
| A04 P0 | Cancelamento procura McpRequestId textual global, sem dono; IDs iguais de clientes distintos podem colidir. | Program.cs:73; Program.RequestLoop.cs:2723 | Alta | M / HIGH | 076 |
| A05 P0 | Frames Worker de progresso são distribuídos às sessões ativas, com token da operação interna. Escopo/token precisam ser verificados no wire. | Program.WorkerLifecycle.cs:222–245 | Alta | M / HIGH | 076 |
| A06 P0 | Há várias threads STA/threads de índice e gate SDK com poucos consumidores. Exclusividade/afinidade global não está demonstrada. Corrida e corrupção são hipóteses, não medições. | Worker/Program.cs:367,393; KbService.cs:579,767,881; SdkGate.cs:11 | Alta no fato; média na consequência | L / HIGH | 077 |
| A07 P1 | Filas de comando e saída usam BlockingCollection sem capacidade. Não há limite nessas filas para rajadas. | Worker/Program.cs:18,19,109,110 | Alta | M / MED | 077 |
| A08 P0 | Benchmark descarta resultado RPC; baseline inválida gera aviso e pode terminar 0. O gate pode aceitar desempenho de uma falha. | scripts/bench-live-http.py:369,414 | Alta | S–M / LOW | 074 |
| A09 P0 | Gate Worker live seleciona resolução de tipos; alguns testes “live” de pattern ainda não executam o fluxo SDK. Cobertura unitária não resolve persistência. | scripts/test-live.ps1:131; PatternApplyServiceTests.cs:224; PatternParityHarnessTests.cs:126 | Alta | L / MED | 074,081 |
| A10 P0 | Classificação de operações divide-se entre OperationClassifier, IsMutatingTool e lista legada de idempotency. A manutenção continua exigindo sincronização manual. | OperationClassifier.cs:14; Program.ToolPayload.cs:294; IdempotencyMiddleware.cs:14 | Alta | L / HIGH | 075 |
| A11 P1 | Nenhuma das 50 ferramentas declara outputSchema; structuredContent já existe, mas installer configura sua omissão. Contrato de saída/projeção precisa ser explícito. | tool_definitions.json; cli/lib/config.js:17; McpHandshakeContractTests.cs:179 | Alta | L / MED | 075,082 |
| A12 P1 | CallerGraph revarre todo índice por alvo; BuildPlan colapsa objetos por nome. Há oportunidade de adjacência por identidade, não de recriar índice secundário já entregue. | CallerGraphService.cs:57; BuildPlanService.cs:86; SearchIndex.cs:52 | Alta | L / HIGH | 080 |
| A13 P1 | Refactor aplica regex a ISource completo e tolera caller que falhou antes de renomear alvo. Strings/comentários e completude exigem regressões. | RefactorService.cs:574,599,610 | Alta | L / HIGH | 080 |
| A14 P1 | Instalador permite continuar sem checksum em WebException; exceção de release antiga não está restrita por versão. | scripts/install.ps1:460 | Alta | M / MED | 084 |
| A15 P1 | Instalador limpa destino antes de extrair; probe não valida exit code no trecho auditado. Falha durante update pode deixar instalação indisponível. | scripts/install.ps1:475–483,508–514 | Alta | L / HIGH | 084 |
| A16 P0 | Nexus repete qualquer callMcp após erro de transporte até três vezes, inclusive ferramentas mutantes. | nexus-ide/src/infra/GxGatewayClient.ts:77–115 | Alta | M / HIGH | 085,079 |
| A17 P1 | Extensão aceita workspace não confiável e BackendManager persiste config antes do gate autoStart; não foi encontrado workspace.isTrusted no src. | nexus-ide/package.json:51; managers/BackendManager.ts:69–121 | Alta | M / HIGH | 085 |
| A18 P1 | Gateway usa .NET8; CLI aceita Node18 e CI usa Node20. Política de suporte requer atualização para a janela da 3.0. | src/GxMcp.Gateway/GxMcp.Gateway.csproj:7; package.json; .github/workflows/ci.yml | Alta | L / MED | 084 |
| A19 P2 | Documentação atual afirma apenas handshake/2025; código já atende descoberta 2026. Planos de endpoints marcam várias capacidades entregues como gap. | docs/technical_architecture.md:26; McpRouter.cs:33; docs/sdk_coverage_gap_matrix.md:3 | Alta | M / LOW | 081,086 |

Paths sem prefixo nesta tabela: arquivos Gateway em src/GxMcp.Gateway; Services/Models/Helpers GeneXus em src/GxMcp.Worker; classes *Tests nos respectivos projetos de testes. Cada pacote contém paths completos relativos à raiz e os trechos de código necessários para o executor.

**Ordem de correção imediata:** A08/A09 dão evidência confiável; A02/A03/A04/A05/A16 fecham riscos de efeito repetido ou sucesso falso; A01 restaura frescor. Essas correções comportamentais podem ser entregues na série 2.x. Não esperar a 3.0 inteira para corrigi-las.

## Quatro mudanças de produto que justificam a versão MAJOR

### 1. Alterações compostas revisáveis e recuperáveis

Um agente prepara uma mudança em Source/Rules/Variables, recebe diff e versões dos alvos, valida, aplica e consulta receipt durável. Alteração externa entre preview e apply causa conflito explícito. Desconexão não vira licença para repetir escrita. Expandir depois a estrutura/create/pattern, declarando os limites de atomicidade por adapter.

Fundamento: MutationEngine, AtomicAuthoring, snapshots e MutationRecoveryRegistry já oferecem partes desse fluxo. A inovação é um contrato uniforme e recuperação após reinício. Pacote 079; depende de identidade/cache/executor.

### 2. Engenharia GeneXus por referências e evidências

Impacto, rename, planejamento e execução de build compartilham um grafo por identidade nativa, com origem das arestas e revisão. O agente consegue explicar por que um objeto entra no build, quando o índice está incompleto e por que escolheu full. Otimização SQL passa a ligar sugestão à navegação/spec e à medição.

Fundamento: CallerGraph, BuildPlan, SearchIndex, Refactor, Navigation e fast incremental já existem, mas misturam aproximações e nomes. Pacote 080; não criar um compilador GeneXus paralelo.

### 3. Authoring consciente da instalação e paridade demonstrada

O cliente consulta capacidades por KB/GeneXus Update/WWP/gerador, recebe valores válidos de propriedades e distingue “API encontrada” de “persistência certificada”. Patterns e traduções entram por cenários completos após reabertura. DSO e WWP settings globais só avançam depois de prova limitada.

Fundamento: probes, adapters, PatternParityHarness e TranslationsService que ainda devolve ItemDeferred. Pacote 081. A 3.0 pode ser excelente sem prometer cobertura de todas as dezenas de milhares de métodos SDK.

### 4. Agentes resolvendo tarefas com menos chamadas e contexto correto

Contexto de uma Procedure inclui partes necessárias, assinatura, variáveis, referências e diagnósticos da mesma revisão; conteúdo maior vira resource paginável. O resultado é avaliado por tarefa resolvida, ausência de efeitos indevidos, chamadas, tokens e latência. O Nexus serve de cliente de referência para os contratos.

Fundamento: profiles, resources/prompts, read targets, projeção, métricas e IDE já presentes. Pacotes 082/085. Dados via BC são expansão condicionada no 083; typed SQL atual não executa automaticamente regras de negócio da Transaction.

## Arquitetura proposta

~~~mermaid
flowchart TD
  C["Clientes MCP / Nexus / CLI"] --> P["Gateway: contrato 2025 e 2026"]
  P --> R["Registro canônico de operações"]
  R --> Q["Contexto de request + limites + política de efeitos"]
  Q --> L["Operação / task / receipt / recuperação"]
  Q --> V["Cache por KB, modelo e revisão"]
  L --> E["Worker net48 x86: executor SDK"]
  E --> GX["SDK GeneXus 18 -> KB"]
  E --> S["Snapshots de dados puros"]
  S --> I["Índice e grafo incremental"]
  I --> V
  I --> B["Plano de impacto/build"]
  B --> E
  GX --> EV["Eventos com identidade e revisão"]
  EV --> V
  EV --> P
~~~

A separação acima é por responsabilidade, não demanda microserviços novos. O Worker continua local. Acesso a banco de aplicação e runtime BC têm adapters/contratos distintos da KB de design-time.

## Pacotes, dependências e estado

| [74](./074-v3-baseline-confiavel.md) | Baseline de correção, SDK real e performance | P0 | — | VERIFIED_INTEGRATED |
| [75](./075-v3-contratos-operacoes.md) | Registro único de operações e contratos verificáveis | P0 | 74 | VERIFIED_INTEGRATED |
| [76](./076-v3-mcp-isolamento-tasks.md) | Conformidade MCP, isolamento de clientes e tasks | P0 | 75 | VERIFIED_INTEGRATED |
| [77](./077-v3-executor-sdk.md) | Executor GeneXus com afinidade e filas limitadas | P0 | 74, 75 | VERIFIED_INTEGRATED |
| [78](./078-v3-cache-revisoes.md) | Cache coerente por revisão de KB e dependências | P0 | 75 | VERIFIED_INTEGRATED |
| [79](./079-v3-mutacoes-recuperaveis.md) | Operações idempotentes, mudanças compostas e recuperação verificável | P0 | 75, 77, 78 | VERIFIED_INTEGRATED |
| [80](./080-v3-grafo-build-refactor.md) | Grafo semântico, refatoração segura e build incremental comprovado | P1 | 74, 77, 78, 79 | VERIFIED_INTEGRATED |
| [81](./081-v3-genexus-authoring-paridade.md) | Authoring GeneXus orientado a capacidades e paridade | P1 | 74, 75, 77, 79 | VERIFIED_INTEGRATED |
| [82](./082-v3-contexto-observabilidade-avaliações.md) | Contexto eficiente, observabilidade e avaliação de agentes | P1 | 74, 75, 76, 78 | VERIFIED_INTEGRATED |
| [83](./083-v3-dados-business-components.md) | Dados tipados e Business Components com semântica explícita | P2 | 74, 75, 79, 81 | VERIFIED_INTEGRATED |
| [84](./084-v3-runtime-distribuicao.md) | Runtime suportado e atualização transacional verificável | P1 | 74, 75 | VERIFIED_INTEGRATED |
| [85](./085-v3-nexus-cliente-referencia.md) | Nexus IDE como cliente de referência confiável | P1 | 75, 76, 79 | VERIFIED_INTEGRATED |
| [86](./086-v3-aceite-migracao-v3.md) | Integração, migração e gates de lançamento 3.0 | P0 | 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85 | VERIFIED_INTEGRATED |

O fechamento executado está detalhado em
[`docs/v3-integration-evidence-2026-09-06.md`](../docs/v3-integration-evidence-2026-09-06.md).
`VERIFIED_INTEGRATED` fecha o núcleo do programa; a decisão de GA continua
condicionada aos gates externos listados nesse relatório.

~~~mermaid
flowchart LR
  P74["074 Baseline"] --> P75["075 Contratos"]
  P75 --> P76["076 MCP"]
  P75 --> P77["077 Executor"]
  P75 --> P78["078 Cache"]
  P77 --> P79["079 Operações"]
  P78 --> P79
  P79 --> P80["080 Grafo/build"]
  P79 --> P81["081 Authoring"]
  P76 --> P82["082 Contexto/evals"]
  P78 --> P82
  P81 --> P83["083 Dados/BC"]
  P75 --> P84["084 Distribuição"]
  P76 --> P85["085 Nexus"]
  P79 --> P85
  P80 --> P86["086 RC/GA"]
  P81 --> P86
  P82 --> P86
  P83 --> P86
  P84 --> P86
  P85 --> P86
~~~

O manifest é a referência completa de dependências; o diagrama destaca as principais. 083 termina com decisão de spike verificável: uma inviabilidade comprovada permite fechar a decisão sem anunciar BC funcional.

## Coordenação para outros agentes

O integrador mantém uma única tarefa ativa por arquivo/contrato compartilhado. Agentes de análise e testes podem trabalhar em paralelo; branches/worktrees de implementação não autorizam edições concorrentes no mesmo arquivo.

| Grupo de arquivos | Dono da integração | Regra |
|---|---|---|
| tool_definitions, identity/classifier, golden e exemplos | 075 / integrador | Mudanças de outros pacotes entram sequencialmente |
| Program.RequestLoop, Program.ToolPayload, McpRouter, Program.WorkerLifecycle | integrador Gateway | 076/078/079/082 reservam fatias; não executar edits simultâneos |
| Worker Program/Dispatcher/KbService/IndexCacheService | 077 / integrador Worker | 080/081 aguardam executor integrado e revisão atualizada |
| MutationEngine/WritePipeline/snapshots/receipts | 079 | Adapters de 080/081/083 entram depois do contrato |
| CLI/install/release/CI e versions | 084 / integrador | 085 solicita mudanças do manifest; 086 certifica |
| Nexus GxGatewayClient/BackendManager | 085 | 079 define política antes do cliente consumi-la |
| CHANGELOG, docs de migração e índices dos planos | integrador | Um escritor por vez |

Ondas recomendadas:

- **Onda 0:** 074 e reprodução estreita dos P0; nenhuma otimização antes de métricas corretas.
- **Onda 1:** 075; depois 076/078 sequenciais nas partes Gateway, 077 no Worker e 084 nos instaladores em paralelo onde não houver conflito.
- **Onda 2:** 079; 082 pode preparar corpus/instrumentação sem alterar arquivos reservados.
- **Onda 3:** 080 e 081 com reserva de IndexCacheService/Dispatcher; 085 no cliente. 083 faz spike após os pré-requisitos.
- **Onda 4:** 086 integra, executa carga/migração e prepara RC.

Estimativa agregada: cerca de **120–200 dias de engenharia**, incluindo integração e spikes, não custos faturados nem promessa de calendário. Com três executores e revisão dedicada, planejar aproximadamente **14–22 semanas**, reajustadas após 074/077. SDK/WWP/licenciamento e corpus live são os maiores fatores de incerteza. Correções iniciais cabem em fatias bem menores.

### Prompt de delegação

~~~text
Implemente somente o pacote plans/NNN-v3-*.md indicado na atribuição,
na raiz C:\Projetos\Genexus18MCP. Leia o pacote inteiro e AGENTS.md.
Confirme pré-requisitos integrados e compare o commit planejado com HEAD.
Reserve os arquivos com o integrador. Primeiro reproduza o comportamento
em teste; implemente o menor incremento e execute os comandos do pacote.
Use fixture isolada para SDK; preserve arquivos e configurações alheias.
Não faça commit/push/merge/release nem altere dados reais sem autorização.
Entregue: paths e diff, revisão validada, testes executados/resultados,
limitações, estado de persistência quando aplicável e próximo incremento.
~~~

Substituir NNN pelo número exato antes da delegação. Se skills de execução estiverem disponíveis, podem ser usadas; o plano não depende de Superpowers ou outro plugin não instalado.

### Contrato de handoff

O agente devolve objeto equivalente a:

~~~json
{
  "planId": 78,
  "state": "READY_FOR_REVIEW",
  "baseCommit": "revision used by executor",
  "changedPaths": [],
  "checks": [{"command": "actual command", "exitCode": 0, "executedTests": 1}],
  "liveValidation": "passed | unavailable | not_required",
  "knownLimits": [],
  "nextStep": "integrator review"
}
~~~

Valores do exemplo são forma, não execução. Estados: PLANNED -> IN_PROGRESS -> READY_FOR_REVIEW -> VERIFIED_INTEGRATED; BLOCKED exige motivo/ação concreta. Conclusão de agente não prova integração.

## Metas de qualidade e performance

**Invariantes obrigatórios:** zero sucesso falso; zero cancelamento/progresso cruzado; zero resposta stale após mutação confirmada no corpus; zero reexecução automática de escrita incerta; zero skip live obrigatório; zero perda da versão anterior nos faults de instalação.

**Metas propostas, ainda não medidas:** reduzir 25% do p95 do gargalo dominante identificado em 074; reduzir 25% de chamadas ou tokens por tarefa do corpus com sucesso preservado; ausência de crescimento não limitado de filas/memória numa sessão de carga de 8h. Metas de throughput vêm de operações concluídas corretamente, nunca de erros rápidos.

**Gate provisório de não regressão:** investigar aumento superior a 10% de p95/RSS/bytes no mesmo cenário, hardware, revisão de fixture, perfil e tamanho de amostra. Uma correção de integridade que custa latência pode ser aceita com justificativa explícita; não voltar a cache incorreto para cumprir percentual.

O benchmark deve registrar amostras bem-sucedidas/falhas/skips separadamente, p50/p95, tempo cold, cache cold/warm, concorrência, volume da KB, gerador e revisão de código. Não há base nesta auditoria para prometer “10x mais rápido”.

## Fronteira de escopo da GA

**Obrigatório:** 074–080, 082, 084–086 nos seus cortes definidos; em 081, matriz de capacidades e certificação dos patterns já suportados são obrigatórias. Traduções/DSO/WWP avançado exigem a decisão de spike, e só entram como funcionalidade se o roundtrip for comprovado. 083.1 esclarece typed SQL e 083.2–4 entregam decisão de viabilidade, sem exigir BC funcional. Profiles/core existentes continuam suportados. Não negociar corretude para caber no cronograma.

**Condicionado a prova SDK e demanda:** BC runtime/multilevel, WWP Settings/Components, DSO amplo, novas combinações SDK/gerador e formas avançadas de merge. Registrar como unsupported/deferred até a certificação.

**Fora do programa:** SaaS/cloud remoto multi-tenant, cobrança, marketplace de plugins, MCP como executor arbitrário de SQL/shell, migração do SDK Worker para .NET10, “cobrir todos os métodos SDK”, parser/compilador GeneXus completo, reescrita da IDE.

Refactors de arquivos grandes só são aceitos quando extraem um contrato que o pacote usa. Program.RequestLoop e ObjectService grandes são sinais de concentração, não justificativa independente para reescrever.

## Reconciliação dos planos anteriores

| Histórico | Decisão desta análise |
|---|---|
| 001–003 / 006 | Índices/flush/predicados já entregues; 078/080 tratam revisão/dependência/identidade, não repetem aquelas otimizações |
| 005 / 007 / 008 / 009 | Dispatch table, decomposição e caracterização já entregues; reutilizar testes e seams |
| 046 | ToolIdentity existente; continuar, não criar registro concorrente |
| 047 | Parcial: live gate existe, mas falta cobertura real de persistência/paridade; execução futura consolidada em 074 |
| 048–049 | Parcial/necessita reconciliação: há OperationClassifier e guard recente, mas decisões ainda fragmentadas; consolidar em 075 |
| 050 | Traduções ainda ItemDeferred; 081 consolida a trilha e mantém DSO como spike |
| 051–067 | Extensão, CSP, providers e testes já implementados; 085 foca contrato/retry/trust, sem reapresentar recursos |
| 068–072 | Correções constam DONE; não replanejadas como ausentes |

As linhas históricas de plans/README.md permanecem como proveniência. Seu texto “aguardando” ou “não lançado” pode referir-se à data daquela auditoria, não ao snapshot atual. Não marcar plano antigo DONE apenas porque esta análise o substituiu.

## Considerado e rejeitado ou reclassificado

- “Migrar tudo para SDK oficial MCP”: não selecionado como premissa. Primeiro fechar contrato/conformance; adoção de biblioteca é spike com comparação de compatibilidade e custo, não valor de produto por si.
- “Implementar MCP 2026 do zero”: rejeitado; já há suporte parcial no código.
- “Criar structuredContent”: rejeitado; já existe. O problema é contrato/schema e decisão de projeção.
- “Criar cache, índices secundários, jobs, circuitos de recuperação, dry-run ou testes”: rejeitado como descrição do estado atual; elevar garantias existentes.
- “Localhost sem auth é vulnerabilidade automaticamente”: rejeitado por decisão documentada. Isolamento, Origin/Host, conteúdo e efeitos continuam requisitos; serviço remoto exigiria outro threat model.
- “Trocar reflection por typed em tudo”: rejeitado. Packages opcionais, versões e formas SDK demandam adapters; promover typed quando suportado e certificado.
- “Transação de dados tipados é Business Component”: rejeitado. Runtime de aplicação e regras são domínio separado.
- “Bytecount de schema é token count”: rejeitado; medir wire/modelo no corpus.
- “Todos os gaps nos roadmaps SDK são atuais”: rejeitado por drift visível e serviços já implementados.

## Validação desta entrega e limites da auditoria

**Executado nesta sessão:**

- git status --short inicial: limpo; revisão b3d20f7.
- Inventário de fontes, schemas, testes, tooling, instruções e histórico 001–072.
- Leitura do caminho Gateway -> router -> dispatcher -> serviço em áreas críticas de protocolo, cache, escrita, índice/build e dados.
- npm test: **79 passaram**, 0 falhas, 0 skips.
- npm run lint: exit 0.
- npm audit --json na raiz: **0 vulnerabilidades reportadas**. Isso não cobre NuGet, SDK proprietário ou dependências da extensão.
- Consulta a documentação primária atual de MCP, Microsoft, Node, VS Code e GeneXus.
- Artefatos de planejamento passam validação de JSON, dependências acíclicas, links/paths e diff; o resultado dessa validação é registrado na resposta final.

**Não executado:** build .NET, suites completas Gateway/Worker/VSIX, cobertura, testes live, benchmarks de KB, carga 8h, probes de SDK, aplicação de pattern, operação em banco, instalador ou release. Não inferir resultados desses checks a partir dos testes da CLI.

A cobertura foi transversal por pacote e caminhos de maior risco; não uma prova formal de todas as linhas nem auditoria de assemblies Artech/WWP, DLLs binárias distribuídas, segredos de configuração ou infraestrutura externa. As oportunidades de performance são propostas fundamentadas no código; tamanho do ganho e gargalo dominante ficam a cargo de 074. Achados auxiliares foram relidos antes de inclusão; a revisão final dos planos foi feita pelo agente principal.

## Referências primárias consultadas em 2026-09-05

- [MCP 2026-07-28, mudanças de revisão](https://modelcontextprotocol.io/specification/2026-07-28/changelog): contrato moderno muda sessão/handshake, assinaturas e metadata; manter caminhos separados por revisão.
- [MCP Tools 2026](https://modelcontextprotocol.io/specification/2026-07-28/server/tools): input/output schemas e resultado estruturado sustentam contratos de 075.
- [MCP transports 2026](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports): metadados no corpo e cancelamento específico por transporte.
- [Tasks Extension, SEP-2663](https://tasks.extensions.modelcontextprotocol.io/seps/2663-tasks-extension): extensão opt-in atual usa get/update/cancel; não implementar somente a variante experimental antiga.
- [Política de suporte .NET](https://dotnet.microsoft.com/en-us/platform/support/policy): .NET8 até 10/11/2026, .NET10 até 14/11/2028; Worker net48 exige análise separada.
- [Node releases](https://nodejs.org/en/about/previous-releases): Node22/24 estão LTS e 18/20 EOL no snapshot consultado.
- [VS Code Workspace Trust](https://code.visualstudio.com/api/extension-guides/workspace-trust): limitar efeitos em modo restrito antes de inicialização.
- [GeneXus Business Component Save](https://wiki.genexus.com/commwiki/wiki?23229%2CBusiness+Component+Save+method=): semântica de BC pertence à execução da aplicação; viabilidade de adapter ainda precisa de spike.
