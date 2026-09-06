# 74 — Baseline de correção, SDK real e performance

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Fazer o gate falhar em respostas erradas e produzir uma comparação reproduzível da versão 2.57.0 com cada incremento da 3.0.

**Arquitetura:** Ampliar scripts/test-live.ps1 e scripts/bench-live-http.py existentes. Separar contratos sem SDK, integração com SDK instalado e cenários numa KB de teste provisionada; a execução live exige identificação inequívoca da KB descartável.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P0 | L · 4–7 dias de engenharia | MED · harness live pode tocar KB/processos; isolamento é condição de entrada | nenhuma | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "scripts/bench-live-http.py" "scripts/test-live.ps1" "scripts/mcp_llm_contract_smoke.ps1" "src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs" "src/GxMcp.Worker.Tests/PatternApplyServiceTests.cs" "src/GxMcp.Worker.Tests/PatternParityHarnessTests.cs"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-74-baseline-confiavel; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- scripts/bench-live-http.py:369: run_op recebe elapsed e descarta o resultado RPC; uma resposta de erro pode alimentar a amostra.
- scripts/test-live.ps1:131: o gate Worker seleciona apenas TryResolveTypes_finds_GeneXus_tasks_when_SDK_installed.
- src/GxMcp.Worker.Tests/PatternApplyServiceTests.cs:224 e PatternParityHarnessTests.cs:126 mantêm cenários live sem aplicação/abertura real.
- Já existem E2ELiveSmokeTests, smoke HTTP, benchmarks e floors de cobertura. O plano 047 está parcialmente atendido; completar seus objetivos, sem criar outro launcher.

Trecho atual do benchmark:
~~~python
el, _ = rpc(session_id, "tools/call", {
    "name": args_dict["name"],
    "arguments": args_dict["arguments"],
}, timeout=120)
samples.append(el)
~~~

## Arquivos em escopo

- scripts/bench-live-http.py
- scripts/test-live.ps1
- scripts/mcp_llm_contract_smoke.ps1
- src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs
- src/GxMcp.Worker.Tests/PatternApplyServiceTests.cs
- src/GxMcp.Worker.Tests/PatternParityHarnessTests.cs
- scripts/tests/test_bench_live_http.py (novo)
- docs/live-kb-test-harness.md (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **74.1 — Provar que o benchmark rejeita falsos ganhos.** Criar scripts/tests/test_bench_live_http.py com unittest da biblioteca padrão. Exercitar resposta JSON-RPC error, MCP isError=true, envelope de domínio malsucedido, resultado vazio inesperado e operação omitida. O teste deve injetar rpc falso, sem gateway, e exigir código de saída não zero no modo gate. Reutilizar envelope_is_ok e distinguir sucesso vazio legítimo por operação. Verificação: python -m unittest discover -s scripts/tests -p "test_bench_live_http.py" -v; reproduzir a falha antes da alteração.
- [x] **74.2 — Corrigir o protocolo de medição.** Acrescentar contadores attempted/succeeded/failed/skipped por operação, p50/p95 e bytes, mantendo amostras brutas sem conteúdo de KB. Comparar a mesma população: operação, parâmetros, KB fixture, revisão, gerador, tamanho, cache cold/warm e concorrência. Baseline ausente/inválida ou operação faltante torna --fail-on-regression uma falha. Não misturar falhas com latências de sucesso. Executar novamente o unittest; todos os casos devem passar.
- [x] **74.3 — Tornar o harness seguro e completo.** Em test-live.ps1, validar o manifest da fixture e registrar PID/descendentes próprios, sem matar todos os Workers do diretório publish. O seed deve ser sintético e provisionado por caminho GeneXus/XPZ verificado: copiar a pasta de uma KB vinculada ao mesmo SQL Server não prova isolamento. Criar manifest da fixture no próprio plano de implantação com IDs e destinos anonimizados. Se não houver licença/SQL/seed, terminar com live=unavailable e gate de release reprovado; nunca converter em passagem. Escopo integrado: o manifest explícito, o isolamento, o registro de PID/descendentes próprios e a fixture KBTeste foram executados; seed XPZ/GeneXus independente, WWP e três tamanhos de KB permanecem gates pré-GA.
- [x] **74.4 — Implantar um corte vertical live.** Criar Procedure sintética, gravar Source, forçar releitura, reiniciar somente o Worker da fixture e confirmar Source novamente. Em cenário separado, aplicar/reaplicar pattern e comparar família/partes com baseline IDE. Usar os métodos e tipos de teste existentes; adicionar Traits LiveE2E aos cenários reais. Verificação: filtro LiveE2E com fixture configurada, cenário esperado executado e zero skip obrigatório. Escopo integrado: o corte vertical create/write/read/reload/delete e o benchmark live da fixture foram executados; pattern parity WWP e a matriz sem skips obrigatórios permanecem gate pré-GA.
- [x] **74.5 — Registrar baseline antes de otimizar.** Medir 3 repetições cold e ao menos 100 amostras warm por operação (o teto atual -Iterations=100 comporta o lote), em KBs sintéticas pequena/média/grande; selecionar alvos com e sem homônimos. Guardar hardware, versão SDK/WWP, percentis, erros, RSS e revisão. Um p95 com apenas 12 amostras não decide otimização. Atualizar docs/live-kb-test-harness.md com comandos e localização dos resultados ignorados pelo Git. Escopo integrado: o baseline formal warm100 da KBTeste foi registrado; três KBs, cold triplo, homônimos e RSS completo permanecem expansão pré-GA.

## Contratos de teste e oráculos

Oráculos mínimos, executáveis por testes com fakes:
~~~python
cases = [
    ("jsonrpc_error", False),
    ("tool_is_error", False),
    ("domain_error", False),
    ("valid_empty_list", True),
    ("missing_requested_operation", False),
    ("invalid_baseline_in_gate_mode", False),
]
~~~
O executor deve fazer cada caso chegar ao run_op/saída real do harness, sem testar somente uma função duplicada do parser. Modelo C#: E2ELiveSmokeTests e os comparadores reais de PatternParityHarnessTests.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
python -m unittest discover -s scripts/tests -p "test_bench_live_http.py" -v
npm test
npm run lint
# Somente após validar a fixture isolada:
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'
pwsh -NoProfile -File scripts/test-live.ps1 -KbPath $env:GXMCP_TEST_KB -RunBenchmark -Iterations 100
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Erro de ferramenta nunca é contado como ganho de desempenho; o benchmark formal terminou com 700/700 operações válidas.
- [x] Gate live comprova escrita após reabertura e recusa skips de operações obrigatórias; WWP/navegação ausentes são skips condicionais declarados no relatório.
- [x] Benchmark falha se baseline ou operação exigida faltar; resultados incluem população, revisão, hardware e percentis.
- [x] Cleanup identifica recursos próprios; nenhuma configuração de cliente real é alterada.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com paths, comandos, resultados e limitações.
- [x] Integrador atualizou o estado no manifest; o status integrado mantém os gates externos explícitos.

## Condições de parada

KB de teste compartilhando datastore/identidade com KB do usuário; licença ausente; runner sem SDK; mesmo baseline não reproduzível. Relatar essas condições, sem substituir o teste live por mocks.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Reexecutar a baseline quando mudarem fixture, runtime, gerador ou contrato de payload. Resultados anteriores perdem comparabilidade; preservar sua proveniência.
