# 83 — Dados tipados e Business Components com semântica explícita

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Separar claramente manutenção de dados por SQL tipado e execução de regras de negócio GeneXus, oferecendo uma prova limitada de Business Components sem alterar o contrato records_* existente.

**Arquitetura:** Manter TransactionRecordsService como adapter de datastore com receipts já implementados. Investigar adapter BC no runtime da aplicação gerada/endpoint autorizado, fora do Worker de design-time; só promover após demonstrar regras, transação e retorno de mensagens.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P2 · direção de produto, spike primeiro | L · 5–8 dias de spike; implementação posterior estimada em 10–20 dias | HIGH · dados de aplicação, regras e runtime distinto | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md), [79](./079-v3-mutacoes-recuperaveis.md), [81](./081-v3-genexus-authoring-paridade.md) | VERIFIED_INTEGRATED (typed SQL + BC deferido) |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Worker/Services/TransactionRecordsService.cs" "src/GxMcp.Worker.Tests/TransactionRecordsServiceTests.cs" "src/GxMcp.Worker.Tests/TransactionRecordsFakeDatabase.cs" "docs/transaction-records.md"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-83-dados-business-components; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- TransactionRecordsService.cs:190 abre conexão ADO.NET, inicia transação Serializable e :212 chama ExecuteWrite.
- docs/transaction-records.md descreve SQL tipado por metadados SDK, receipts single-use, confirmação pós-commit e proibição de compensação automática depois do commit.
- O escopo atual é root level e uma linha por operação; SQL Server/Oracle têm adapters definidos. Isso é contrato deliberado, não defeito por falta de CRUD genérico.
- Business Component.Save é runtime da aplicação e executa lógica da Transaction; não equivale a usar KBObject.EnsureSave no SDK de design-time.

Trecho atual:
~~~csharp
using (var tx = connection.BeginTransaction(IsolationLevel.Serializable))
...
ExecuteWrite(connection, tx, db, isInsert, normalizedFilters, normalizedValues, snapshot, out generatedKey, timeout);
~~~

## Arquivos em escopo

- src/GxMcp.Worker/Services/TransactionRecordsService.cs
- src/GxMcp.Worker.Tests/TransactionRecordsServiceTests.cs
- src/GxMcp.Worker.Tests/TransactionRecordsFakeDatabase.cs
- docs/transaction-records.md
- docs/business-component-adapter.md (novo)
- src/GxMcp.Gateway.Tests/BusinessComponentContractTests.cs (novo, apenas contrato após spike)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **83.1 — Tornar as semânticas explícitas.** Adicionar metadado de execução data_access/typed_sql e businessRulesExecuted=false quando esse for o caminho, sem mudar segurança dos receipts. Documentar limitações de níveis, triggers, domínio e providers. Testar que a comunicação não promete regras BC em operações SQL. Implementado nos envelopes de leitura, preview, sucesso e erro de `TransactionRecordsService`, com regressão de query.
- [x] **83.2 — Provar adapter BC em ambiente descartável.** O estudo foi encerrado como indisponível: a fixture `C:\kbs\KBTeste` e o checkout não fornecem aplicação .NET gerada, endpoint autorizado ou runtime de BC a validar. A ausência é registrada em `docs/business-component-adapter.md`; não foi exposto adapter funcional nem inferida semântica de regras a partir do SDK de design-time.
- [x] **83.3 — Definir separação de contexto e autorização.** Contrato do spike inclui target application/environment, operação, identidade da Transaction/BC, preview suportado ou indisponível, policy de rollback e limites. Receipt de typed SQL nunca autoriza BC e vice-versa. Não gerar, compilar, publicar ou executar app real implicitamente para fazer o adapter funcionar. A separação está documentada em `docs/business-component-adapter.md`.
- [x] **83.4 — Decidir a expansão com evidência.** Produzir docs/business-component-adapter.md com assinatura/transport reais, build/runtime alvo, teste de regra, teste de rollback e limitações. Só então decompor implementação do adapter, nível filho e batch numa nova fatia revisável. Nesta 3.0, o gate obrigatório é contrato honesto do typed SQL; BC funcional é condicionado ao spike aprovado. A decisão atual é manter o adapter deferido porque a fixture/runtime autorizados não existem neste checkout.

## Contratos de teste e oráculos

Experimento discriminante:
~~~text
Fixture: Transaction sintética com regra que rejeita quantidade negativa.
typed_sql dryRun: valida tipos/escopo; não afirma regra executada.
BC Save inválido: mensagem da regra; nenhuma linha persistida.
BC Save válido: keys + mensagens + reread no destino correto.
Trocar environment/valores depois do preview: rejeitado.
Falha pós-commit: sem compensação automática.
~~~
A regra do BC deve vir do runtime real. Fake de banco não demonstra semântica de Business Component.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~TransactionRecords"
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~TransactionRecord"
# O comando live do BC deve ser escrito no ADR somente após confirmar o endpoint
# e fixture reais; não existe ainda uma CLI BC no repositório.
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] docs/transaction-records.md e respostas distinguem validação de tipos e regras de negócio; os envelopes identificam `typed_sql` e `businessRulesExecuted=false`.
- [x] Regressões existentes de single-use receipt, concorrência e pós-commit passam: 64 testes Worker e 15 testes Gateway de TransactionRecord.
- [x] Spike BC entrega inviabilidade explícita, sem alegar adapter implementado.
- [x] Nenhum comando implícito de build/reorg/deploy foi adicionado a `records_*`; o caminho permanece limitado ao adapter ADO.NET e seus receipts.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída nos arquivos do pacote.
- [x] Handoff entregue em `scratchpad/v3-plan-audit-2026-09-06.md`, com paths, comandos, resultados, limitações e entrada correspondente no CHANGELOG.
- [x] Integrador atualizou o estado no programa e no manifest; nenhum adapter BC funcional foi anunciado.

## Condições de parada

Qualquer experimento acessar base produtiva; confusão design-time/runtime; necessidade de criação de infra externa ou credencial. Propor o experimento concreto isolado para autorização antes de prosseguir; nunca usar a KB do usuário como fixture.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

BC não é justificativa para enfraquecer receipts SQL nem adicionar compensação pós-commit. Manter providers/multilevel como contratos separados e testados.
