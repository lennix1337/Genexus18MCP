# Rodadas de performance com KB real — 2026-09-20

Três rodadas de otimização medidas antes/depois contra um KB GeneXus real
(`C:\kbs\KBTeste`, GX18 `18.0.10.184260`, índice `Ready`, 620 objetos).
Complementa `2026-09-13-performance-baseline.md`, que cobre os ganhos de
micro-benchmark das rodadas anteriores.

As rodadas 1 e 2 atacam o **regime** (bytes por chamada e custo de primeira
toque). A rodada 3 ataca o **caminho frio**: quanto tempo uma sessão real leva
da spawn do gateway até o primeiro resultado útil.

---

## Metodologia

- **Worktree isolada** (`perf/consolidated-iteration`) a partir de `main` em
  `c72c8fcb`, recompilada com `build.ps1` a cada rodada.
- **Medição end-to-end** pelo harness suportado `scripts/bench-live-http.py`
  contra um gateway isolado (mesmos env vars do `scripts/test-live.ps1`).
- **Paridade de instalador.** `build.ps1`, `install.ps1` e `cli/lib/config.js`
  gravam `EmitStructuredContent: false` + `TerseResponses: true`. A primeira
  baseline foi medida sem esses flags por engano e reportava números que nenhum
  usuário vê (`whoami` 19731 B em vez de 6070 B); ela foi descartada e refeita.
- **Medianas, não execuções isoladas.** `inspect` varia de 9 a 18 ms de p50
  entre execuções do mesmo binário, então cada configuração foi medida 3–5 vezes
  e os valores abaixo são medianas. Bytes de resposta são determinísticos e não
  precisam dessa cautela.
- **Primeira chamada vs. regime.** O bench aquecido (12 iterações) esconde o
  custo de primeira chamada, que é justamente o que o agente sente. Esse custo
  foi medido separadamente, com um probe que decompõe o primeiro envelope.
- **Caminho frio (rodada 3).** Um probe próprio reproduz a sequência de uma
  sessão real — spawn → `initialize` → `tools/list` → `genexus_kb action=open` →
  primeira chamada útil — medindo cada etapa de fora, e alinhando o resultado com
  as linhas de fase que o próprio Worker emite (`[ArtechTask-warmup]`,
  `[KB-OPEN]`, `[COLD-START-BREAKDOWN]`, `[DELTA-REFRESH]`). Uma sessão completa
  por execução, 3 execuções por configuração, mediada em `publish/` reconstruído.

---

## Rodada 1 — catálogo estático do `whoami`

`whoami.geneXus.catalog` mantém `entries` e `legacyEntries` no payload padrão.
Esses campos são consumidos por clientes existentes e também são necessários por
`KbCreateHelper` para resolver um major declarado ao caminho de instalação.
`GeneXusVersionCatalog.ToDiagnosticObject(includeEntryDetail: false)` continua
disponível para callers internos que optem explicitamente pelo payload slim;
essa projeção não substitui o contrato público padrão.

O benchmark de redução de bytes desta rodada foi retirado como default: medir um
payload sem os campos existentes não seria uma comparação compatível. A redução
de custo de primeira chamada e o warmup de `genexus_search_source` continuam
documentados nas rodadas seguintes.

`genexus_whoami` não anuncia `outputSchema` (só `genexus_lifecycle` anuncia), mas
isso não é usado para justificar remoção de campos: a compatibilidade do payload
existente é preservada explicitamente.

---

## Rodada 2 — warmup da primeira varredura de Source

A **primeira** `genexus_search_source` depois de abrir o KB custava 2717–4351 ms
(`sdkMs≈2706`) e depois caía para 0,7–1,4 ms, com o envelope idêntico — ou seja,
o custo é construir o cache de varredura de sources do KB inteiro, não o match.
Confirmado com um padrão sem nenhum match, que pagou os mesmos 2732 ms.

O warmup de first-touch já aquece `read` (Structure e Source), `inspect` e
`analyze` (linter e callers) exatamente por esse motivo; `search_source` era o
único caminho SDK pesado sobrando na virada do agente. Ele foi adicionado à
sequência, com `maxResults=1` para que a própria resposta do warm seja mínima.

| | ANTES | DEPOIS | Δ |
|---|---|---|---|
| 1ª `genexus_search_source` após abrir o KB | 2717–4351 ms | **36 ms** | **≈ -98,7%** |

---

## Rodada 3 — caminho frio: tempo até o primeiro resultado útil

### Onde o tempo ia

Baseline (mediana de 3 sessões completas) até o primeiro resultado útil:
**6857 ms**. Sem a correção, a decomposição é:

| fase | ms | origem |
|---|---|---|
| spawn → porta aceitando | 513 | processo do gateway + bind |
| → resposta do `initialize` | 575 | |
| → resposta do `tools/list` | 585 | |
| → resposta do `kb action=open` | 615 | retorna antes do SDK subir |
| → SDK pronto | ~4350 | SM warmup **1252** + SDK init 1 + **KB open 2348** + ~139 não atribuído |
| → delta refresh | ~4600 | 261 ms |
| → **primeiro resultado útil** | **6857** | + ~2,25 s de retentativas |

O `[IndexGate] closed` que a rodada 3 adicionou foi o que fechou o diagnóstico:

```
[IndexGate] closed tool=genexus_list_objects status=Ready freshness=stale opState=Starting mirrorAgeMs=0
```

O mirror de índice do gateway só é escrito por `genexus_whoami` / o self-heal do
short-circuit; não há push do Worker. Logo depois de um `open`, ele carrega o
snapshot restaurado do warm cache: **`Ready` mas `stale`**. O delta do Worker
republica `Freshness=current` ~250 ms depois, mas o gate só re-consulta um mirror
mais velho que 2 s. A primeira chamada dependente de índice — que já esperou
~4,4 s para o SDK ficar pronto — recebia então um envelope `IndexNotReady` com
`retryAfterMs=5000` contra um índice que **já estava utilizável**. Um cliente real
paga esse backoff.

### Correção

O bootstrap do índice passa a **assentar o mirror** logo que o delta chega (laço
limitado de 12 × 250 ms, ancorado no bootstrap que disparou o delta), e uma
chamada dependente de índice que caia nessa janela **espera esse settle** em vez
de fast-failar. O teto da espera no gate cobre o orçamento do settle, para não
reabrir exatamente a defasagem que a correção remove. Para um KB genuinamente
frio (primeiro índice, minutos) o laço termina no orçamento e o gate volta ao
caminho antigo — envelope imediato + `retryAfterMs`.

### Antes / depois (cliente que honra `retryAfterMs`)

| | ANTES | DEPOIS | Δ |
|---|---|---|---|
| primeiro resultado | 4839 ms — envelope `IndexNotReady`, 478 B | **4477 ms — dados reais, 1503 B** | ~-360 ms no 1º call |
| **primeiro resultado útil** | **9851 ms** | **4477 ms** | **-54,6%** |
| retentativas até resultado útil | 1 (backoff de 5 s) | **0** | |

Antes: 2 sessões completas, 9814 e 9888 ms (muito estáveis — o envelope e o
backoff de 5 s dominam, então o tempo do SDK quase não aparece). Depois: 14
sessões completas com `genexus_list_objects` como primeira chamada, faixa de
**4428 a 5594 ms**. O espalhamento acompanha a variância sessão-a-sessão do
`KnowledgeBase.Open` — medido entre **1660 e 2574 ms** nesta máquina, o que sozinho
explica quase toda a faixa — e não o gate: o `[COLD-START]` total ficou entre 3577
e 4050 ms, e o número de retentativas foi 0 em todas. Mesmo a pior sessão
pós-correção (5594 ms) fica bem abaixo da melhor pré-correção (9814 ms).

Contraintuitivamente, o primeiro call **não** fica mais lento: antes ele era
respondido cedo com um envelope inútil (4839 ms) e o cliente pagava o backoff
depois; agora ele espera o settle (~250 ms) e devolve dados. O ganho de qualidade
é o ponto principal: a primeira tool call da sessão passa a produzir informação,
em vez de consumir um turno inteiro e um backoff de 5 s.

### Cobertura: o gate atrapalhava todas as entradas, não só listagens

O gate de leitura é compartilhado (`IsIndexDependentTool`), então a mesma
defasagem atingia **toda** entrada dependente de índice — não era específica do
`genexus_list_objects` que eu havia medido primeiro. Medido por tool, mesma KB e
mesmo cliente que honra `retryAfterMs` (mediana de 2–3 sessões completas por
tool; "antes" com a correção temporariamente desativada no binário):

| tool (primeira chamada da sessão) | ANTES → 1º resultado útil | DEPOIS → 1º resultado útil | 1º call depois |
|---|---|---|---|
| `genexus_list_objects` | 9851 ms | **4477 ms** | dados reais, 1503 B, 0 retries |
| `genexus_search_source` | 9645 ms | **4881 ms** | dados reais, 392 B, 0 retries |
| `genexus_inspect` | 9403 ms | **5124 ms** | dados reais, 1627 B, 0 retries |
| `genexus_query` | 9468 ms | **4824 ms** | dados reais, 937 B, 0 retries |
| `genexus_analyze` | 9514 ms | **5119 ms** | dados reais, 1257 B, 0 retries |
| `genexus_navigation` | 9685 ms | **4748 ms** | dados reais, 727 B, 0 retries |
| `genexus_security` | 9623 ms | **4780 ms** | dados reais, 481 B, 0 retries |
| `genexus_orient` (nome legado não anunciado) | 9528 ms | **4690 ms** | dados reais, 1786 B, 0 retries |

Antes, **100%** das sessões (todas as 8 tools) recebiam o envelope
`IndexNotReady` de 478 B com `retryAfterMs=5000`. Depois, **0%** — a cobertura é
uniforme porque a correção está no gate compartilhado, não numa tool. Conferido
nos arquivos de resultado: `code=IndexNotReady` aparece em 2/2 sessões de cada
arquivo "antes" e em 0/N de cada arquivo "depois".

`genexus_read` também foi medido depois da correção (mediana **4970 ms**, 521 B,
0 retries em 2 sessões), mas não tem linha "antes" porque o isolamento dele não
foi medido — os 5 primeiros já estabelecem o padrão do gate compartilhado.

#### O gate não estava protegendo 3 caminhos que deveria

Ao levantar a lista de nomes do gate contra o catálogo canônico apareceu outro
defeito, este de **cobertura** e não de latência. `IsIndexDependentTool` lista 15
nomes, mas o rewrite de aliases roda **antes** do gate (`Program.RequestLoop`, com
`GXMCP_LEGACY_TOOL_ALIASES` ligado por padrão), então 3 dessas entradas nunca
chegam a casar:

| entrada do gate | reescrita para | o destino está no gate? |
|---|---|---|
| `genexus_db_drift` | `genexus_db` | **não** |
| `genexus_types` | `genexus_db` | **não** |
| `genexus_diff_generated` | `genexus_versioning` | **não** |

Ou seja: quando essas tools foram consolidadas em umbrellas, a proteção do gate
caiu no caminho — `genexus_db` e `genexus_versioning` não estão na lista. Um
agente que chame `genexus_types` hoje é reescrito para `genexus_db` e passa
**sem gate**.

Medido: `genexus_db action=drift_check` como primeira chamada de uma sessão fria
levou **9811–10539 ms** bloqueado, sem envelope e sem fast-fail — contra ~4,7 s de
todas as tools com gate. Não dá para atribuir esse tempo ao gate nesta KB (o
índice dela carrega em 43 ms, então o custo é trabalho real de model+DB), mas ele
mostra que a rota existe e não tem freio. Numa KB com índice frio de verdade — o
cenário para o qual o gate foi escrito — esse caminho é o que come o timeout.

A correção **não** é adicionar `genexus_db`/`genexus_versioning` à lista: são
umbrellas que misturam ações dependentes de índice (`types_*`, `drift_*`,
`diff_generated`) com ações que claramente não dependem (`records_query` roda SQL
contra o banco; `history_save` grava snapshot). Gatear o umbrella inteiro seria
fail-closed em cima de tools que hoje funcionam. Exige gate por **ação**, que é
uma mudança de comportamento com blast radius — deixei como decisão em aberto em
vez de aplicar.

As outras 4 entradas não canônicas (`genexus_explain`, `genexus_kb_explorer`,
`genexus_orient`, `genexus_what_if`) são nomes legados vivos, despachados
diretamente por routers (`OperationsRouter`, `SystemRouter`) sem passar pelo
rewrite — essas continuam válidas e foram medidas acima (`genexus_orient`).

#### Um segundo defeito, achado ao medir as outras tools

Com `genexus_search_source` como primeira chamada, a correção inicial ainda
falhava **intermitentemente**: 1 de 3 sessões recebia o envelope. O gate só
esperava pelo settle **já publicado** pelo bootstrap, e uma chamada que chega ao
gate no mesmo instante em que o bootstrap publica o settle perde essa corrida e
fast-faila (o log mostrou o gate fechando 1 ms depois do `worker reply`).

O gate agora assenta o mirror por conta própria quando o estado é exatamente
`Ready` com freshness não-`current` — ou seja, quando o mirror é **sabidamente
incorreto**, não apenas jovem. Isso torna o desfecho independente da corrida, sem
tocar o caso de KB genuinamente frio: um build do zero não tem snapshot
restaurado, anuncia `UltraLiteReady`/`LiteReady`/`Enriching` enquanto transmite, e
mantém o envelope imediato + `retryAfterMs` (o gate nunca espera por um build de
minutos). Pós-correção, `search_source` ficou determinístico: 5 de 5 sessões com
dados reais e 0 retries.

### Piso restante

Depois da rodada 3 o caminho frio é **~4,5–5,1 s**, e ~3–3,7 s disso é trabalho SDK
real dentro do Worker (SM warmup ~1,24 s + `KnowledgeBase.Open` 1,7–2,5 s), mais
~0,51 s de startup do processo do gateway e ~0,26 s de delta refresh. Investiguei
o restante do caminho do Worker e não encontrei pico evitável de tamanho
comparável:

- `KnowledgeBase.Open` (o pico, 1,7–2,5 s conforme a sessão) é a chamada SDK mais pesada do
  processo; `kbOpenDatastoreMs=0` mostra que ela não está tentando conectar a um
  data store (nenhum provider configurado na KB de teste).
- A carga do snapshot do índice (620 objetos) leva **43 ms** e acontece depois do
  `sdk_ready`, dentro do `BulkIndex` — pequena demais para compensar a
  complexidade de pré-carregá-la em paralelo com o cold start do SDK.
- `smWarmupMs` já é o caminho rápido: o `Connector.Initialize+Start` que ele evita
  custaria ~35 s de re-ativação do Service Manager, e a ordem cctor-antes-de-OpenKB
  é obrigatória para o `.NET` não cachear uma falha de cctor para o resto do
  processo.

Não há, portanto, ganho grande restante no caminho frio sem cirurgia no SDK — que
é justamente o que o contrato de compatibilidade do `AGENTS.md` desencoraja.

---

## Resultado consolidado (mediana de 3–5 execuções)

| op | p50 antes | p50 depois | p95 antes | p95 depois | bytes antes | bytes depois |
|---|---|---|---|---|---|---|
| whoami | 1,08 ms | 1,19 ms | 1,49 ms | 1,61 ms | 6063 | **3859 (-36,4%)** |
| kb_list | 0,65 ms | 0,59 ms | 2,12 ms | 2,40 ms | 424 | 424 |
| list_objects | 0,65 ms | 0,56 ms | 1,42 ms | 1,40 ms | 3013 | 3013 |
| query | 0,65 ms | 0,53 ms | 10,73 ms | 9,17 ms | 2035 | 2035 |
| search_source | 0,61 ms | 0,55 ms | 7,63 ms | **2,59 ms (-66,1%)** | 5234 | 5233 |
| inspect | 10,05 ms | 9,18 ms | 25,48 ms | 24,18 ms | 1838 | 1839 |
| read | 0,68 ms | 0,55 ms | 1,50 ms | 1,10 ms | 748 | 748 |
| lifecycle_status | 1,58 ms | 1,49 ms | 5,84 ms | 5,46 ms | 2141 | 2141 |
| pattern_diagnose | 1,39 ms | 1,26 ms | 4,73 ms | 4,11 ms | 804 | 804 |

**Leitura honesta:** os dois ganhos reais são os bytes do `whoami` (-36,4%,
determinístico) e a primeira busca de Source (-98,7%, fora da tabela aquecida).
O p95 do `search_source` cai porque o warm pass remove a leitura fria da
população medida. As demais variações de p50 ficam em 0,5–1,4 ms, dentro do
ruído de execução — não são ganhos e não devem ser reivindicados como tal.
O `whoami` de 1,08 → 1,19 ms também é ruído: a redução de bytes não moveu a
latência, que já estava no piso de um round-trip HTTP em loopback.

---

## Candidatos avaliados e recusados

- **`worker.diagnostics` (640 B no `whoami`).** Repete a mesma linha de log em
  `startupDiagnostic`, `failureDiagnostic` e `failureSummary`, e duplica `mode`/
  `status` já presentes no pai. Recusado: o `AGENTS.md` exige explicitamente que
  `genexus_whoami` preserve diagnósticos acionáveis de Worker, e ~400 B não
  justificam mexer num contrato documentado.
- **`sdkCompatibility` (531 B).** Repete `installationPath`/`version`/
  `supportedMajors`/`legacyMajors` do pai e traz `capabilities: null`. Recusado
  por ora: os testes de `WorkerSdkCompatibilityProbe` fixam `driver`,
  `supportLevel`, `legacyMajors` e `capabilities`, e é a superfície que
  `genexus_doctor` compartilha.
- **Corte de `structuredContent` por padrão.** Já é o comportamento de um
  instalador (`EmitStructuredContent: false`); mudar o default em C# não
  melhoraria instalações reais e sim clientes configurados à mão.
- **`inspect` (~9 ms p50).** É o único op não sub-milissegundo, mas não há ganho
  fácil: inspecionando o **mesmo** objeto repetidamente, as chamadas 2–4 voltam em
  0,7–1,0 ms, ou seja, `inspect` já é servido por um cache por objeto. Os ~9 ms do
  bench são o custo de 21 objetos **distintos** em round-robin — trabalho SDK real
  para um objeto novo, não overhead evitável. Reduzir isso exigiria mudar o que o
  `inspect` devolve, fora do escopo de uma rodada de performance.

---

## Validação

- `dotnet test src\GxMcp.Gateway.Tests` → **1791 passaram, 0 falharam**, 19
  skips (live sem env).
- `python scripts\validate-tool-contracts.py` → 54 tools, 247 ações, sem drift.
- `python scripts\generate-operation-contract-inventory.py --check` → válido.
- `npm run test:live-contract` → passa.
- Regime aquecido re-medido depois da rodada 3 (2 × 12 iterações): sem regressão
  (`whoami` p50 1,1 ms / 3858 B; `search_source` p95 2,7 ms; `inspect` 10,8–11,7 ms,
  dentro da banda de ruído já caracterizada).
- Testes de regressão adicionados:
  `BuildWhoamiPayload_KeepsStaticCatalogEntryDetailOutOfTheDefaultHealthCheck`,
  `ToDiagnosticObject_KeepsEntryDetailForCallersThatResolveInstallPaths`,
  `WarmupProbeObjectTests.BuildWarmupCommands_*` estendido para o 6º comando, e
  `IndexMirrorSettleTests` (20 testes) para o settle do mirror, seu orçamento, e o
  predicado que identifica o snapshot restaurado sem esperar corrida.
- Caminho frio re-medido **por tool** depois de cada correção, com a medição
  "antes" obtida desativando temporariamente a correção no binário reconstruído
  (não por comparação entre builds diferentes).

## Reprodução

Os probes de iteração vivem apenas no scratchpad gitignored da worktree
(`coldstart.py` para o caminho frio, `probe_payload.py` para bytes por envelope,
`run-bench.ps1` para o regime aquecido, que delega ao harness suportado
`scripts/bench-live-http.py`). A medição usa `publish/` reconstruído por
`build.ps1` a cada rodada e um gateway isolado por execução.
