# Evidência de integração 3.0 — 2026-09-06

Este relatório fecha a integração do núcleo dos planos 074–086 no checkout
atual. O estado `VERIFIED_INTEGRATED` no manifest significa que a implementação
integrada, os contratos determinísticos e os gates disponíveis neste ambiente
foram revisados. Ele não autoriza publicação nem transforma uma dependência
externa ausente em passagem.

## Evidência executada

- Gateway: **1.407 pass, 10 skipped, 0 failed**.
- Worker com `GX_PATH` apontando para GeneXus 18: **2.310 pass, 4 skipped, 0 failed**.
- CLI: `npm test` **79 pass** e `npm run lint` passou.
- Nexus IDE: `npm --prefix src/nexus-ide run check` passou com **111 testes**.
- Scripts: `python -m unittest discover -s scripts/tests -v` passou com **36 testes**.
- Contratos: `validate-tool-contracts.py` passou com **50 tools / 207 actions**;
  inventário operacional passou com **50 tools / 226 operações projetadas**;
  avaliação v3 passou com **15 cenários**; warning baseline passou com **216
  localizações**.
- Wire: `python scripts/mcp-wire-conformance.py` passou em HTTP legacy,
  HTTP sessionless 2026, tasks fail-closed, subscriptions/ack, reconexão,
  consumidor lento, Host/origin e stdio com IDs numérico/string concorrentes.
- Instalação: `node scripts/verify-install.js` e o contrato transacional do
  instalador passaram; o manifesto candidato foi verificado com **5 artefatos**,
  SBOM, hashes, schema e matriz de runtime.
- Release candidate: `3.0.0-rc.1` foi reconstruído em
  `scratchpad/release-candidate-rc1`; `release-manifest` e os dois testes do
  verificador passaram. Nenhuma publicação foi executada.
- Live KB: `C:\kbs\KBTeste` passou o smoke Gateway (8 cenários, 2 skips
  condicionais documentados), o check Worker, o vertical create/write/read/
  reload/delete e o benchmark warm de 100 iterações (**700/700**, zero falhas
  ou skips de operação; p95 de 16,2 ms a 25,4 ms nas operações medidas). O resultado fresco está em `scratchpad/live-kbteste-formal-warm100-current-20260906.json`; `r9` permanece como baseline comparável.
- Grafo: o benchmark executável de 1k/10k/50k objetos confirmou lookup por
  adjacência revisionada na ordem de dezenas de microssegundos, contra scans
  de centenas de microssegundos a milissegundos, com reconstrução linear. A execução fresca cobriu 9 benchmarks em 1k/10k/50k objetos.

## Decisões de escopo integradas

1. O contrato MCP dual, isolamento por transporte/sessão, tasks, subscriptions,
   cache por geração, journal de mutação, change set estreito, executor STA,
   grafo revisionado, validação de propriedades, contexto limitado, manifest,
   instalador e cliente Nexus moderno fazem parte do núcleo integrado.
2. Business Components continuam explicitamente fora das capabilities
   suportadas: a fixture fornecida não contém aplicação/runtime BC autorizados.
   O contrato typed SQL identifica `businessRulesExecuted=false`.
3. Pattern parity WWP, baseline em três KBs isoladas, replay com modelo,
   fluxo visual VS Code real, teste de dois clientes/duas KBs, dois ciclos
   completos de upgrade/rollback e soak de 8 horas são gates externos de
   pré-GA. O ambiente atual não possui WWP, seeds independentes suficientes,
   runner VS Code interativo ou autorização para criar esses recursos.
4. Esses limites estão documentados nos planos 074, 081, 082, 083, 085 e 086;
   nenhum resultado ausente é apresentado como sucesso.

## Artefatos de suporte

- Fixture: `scratchpad/kbteste.fixture.json`.
- Vertical live: `scratchpad/run-live-vertical.py`.
- Benchmark formal atual: `scratchpad/live-kbteste-formal-warm100-current-20260906.json`; baseline: `scratchpad/live-kbteste-formal-warm100-r9.json`.
- Candidato distribuível: `scratchpad/release-candidate-rc1/`.
- Manifest operacional: `docs/operation-contract-inventory.json`.
- Migração: `docs/migration-3.0.md`.

O próximo passo para GA é executar somente os gates externos listados acima
com fixtures e runtimes autorizados; não há alteração automática de KB,
configuração de cliente, tag, push ou publicação neste relatório.
