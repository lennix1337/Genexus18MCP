# 84 — Runtime suportado e atualização transacional verificável

> Plano de execução para agente sem contexto da auditoria. Esta entrega é planejamento; iniciar implementação somente quando o mantenedor solicitar. Executar as checkboxes em sequência, registrar evidência e submeter diff ao integrador. Ferramentas/skills específicas são opcionais; não depender de plugin ausente.

**Objetivo:** Distribuir Gateway/Worker/CLI/VSIX coerentes, verificáveis e recuperáveis, usando runtimes suportados sem mexer na exigência net48/x86 do SDK.

**Arquitetura:** Atualizar Gateway/testes/benchmarks para .NET 10 após matriz de compatibilidade e CLI para Node LTS suportado. Evoluir release.ps1 e instaladores existentes com staging, manifesto de artefatos e rollback, preservando configurações e registro dos clientes.

**Stack:** C#/.NET Gateway, Worker .NET Framework 4.8 x86/STA + SDK GeneXus 18; Node.js/PowerShell; Nexus IDE em TypeScript quando em escopo.

| Prioridade | Esforço estimado | Risco | Dependências | Estado |
|---|---|---|---|---|
| P1 | L · 8–14 dias | HIGH · distribuição e instalação existentes | [74](./074-v3-baseline-confiavel.md), [75](./075-v3-contratos-operacoes.md) | VERIFIED_INTEGRATED |

Planejado em 2026-09-05 sobre commit b3d20f731d47d2e0ae20c179ac0776a047763ade, versão 2.57.0. Esforço é estimativa de planejamento, não medição.

## Decisão de integração 3.0 (2026-09-06)

Estado integrado: `VERIFIED_INTEGRATED` no manifest. O núcleo implementado deste plano está integrado e coberto pelos testes determinísticos e pela evidência consolidada em [v3-integration-evidence-2026-09-06.md](../docs/v3-integration-evidence-2026-09-06.md). Itens que dependem de WWP, seeds de múltiplas KBs, replay de modelo, runner interativo ou soak prolongado permanecem gates pré-GA explícitos; não são capabilities anunciadas nem passam como validação disponível neste checkout.

## Entrada, restrições e passagem

Raiz: C:\Projetos\Genexus18MCP. Ler AGENTS.md, o bloco alvo e seus chamadores/testes. SDK continua sendo fonte de verdade da KB. Não editar arquivos físicos de KB como substituto de persistência SDK. Não usar KB produtiva para testes.

Antes de editar, executar git status --short e comparar a revisão planejada com HEAD:

~~~powershell
git diff --stat b3d20f731d47d2e0ae20c179ac0776a047763ade..HEAD -- "src/GxMcp.Gateway/GxMcp.Gateway.csproj" "src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj" "src/GxMcp.Benchmarks/GxMcp.Benchmarks.csproj" "package.json" "package-lock.json" "src/nexus-ide/package.json" "src/nexus-ide/package-lock.json" "install.ps1" "build.ps1" "scripts/install.ps1" "release.ps1" "cli/lib/config.js" "cli/lib/update-check.js" "cli/run.test.js" "scripts/verify-install.js" ".github/workflows/ci.yml" ".github/workflows/release.yml"
~~~

Mudanças esperadas dos pré-requisitos exigem atualização dos trechos e regressões; divergência sem explicação exige revisão do plano. Novos paths estão explicitamente marcados. Se precisar de arquivo fora do escopo, apresentar a ampliação ao integrador antes de editar.

O integrador controla tool_definitions.json, golden de discovery, CHANGELOG.md, release/CI e plans/README.md. Agentes podem preparar mudanças nesses arquivos, mas somente um os integra por vez. Para cada mudança de comportamento verificada, preparar entrada Unreleased segundo docs/release_protocol.md. Preservar modificações alheias. Branch sugerida: codex/v3-84-runtime-distribuicao; worktree isolada sob worktrees/ se utilizada. Sem commit, push, merge ou publicação por inferência.

## Estado atual e evidência

- Gateway/Tests/Benchmarks têm TargetFramework net8.0-windows. Worker continua net48/x86 por restrição SDK.
- package.json declara node>=18 e CI usa node-version:20.
- scripts/install.ps1:460 aceita WebException de download do SHA com aviso e segue; releases novas não precisam herdar exceção para artefato antigo.
- scripts/install.ps1:475–483 limpa instalação antes de extrair; :508–514 inicia --self-test, mas sucesso do processo não é validado pelo exit code no trecho auditado.
- install.ps1 orquestra configuração/build/registro; build.ps1 é compilação/empacotamento e encerra processos do checkout. Nenhum desses scripts foi executado nesta auditoria.

Trecho atual:
~~~powershell
catch [System.Net.WebException] {
    Write-Warning "Could not download $shaUrl — skipping integrity check (pre-v2.9.2 release or network issue)."
}
~~~

## Arquivos em escopo

- src/GxMcp.Gateway/GxMcp.Gateway.csproj
- src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
- src/GxMcp.Benchmarks/GxMcp.Benchmarks.csproj
- package.json
- package-lock.json
- src/nexus-ide/package.json
- src/nexus-ide/package-lock.json
- install.ps1
- build.ps1
- scripts/install.ps1
- release.ps1
- cli/lib/config.js
- cli/lib/update-check.js
- cli/run.test.js
- scripts/verify-install.js
- .github/workflows/ci.yml
- .github/workflows/release.yml
- scripts/tests/install-contract.tests.ps1 (novo)

Fora do escopo: fontes/configurações/dados não listados, credenciais, autenticação, KBs reais, novas integrações pagas e reformatação transversal. Alterações de schema/contrato compartilhado são integradas pelo responsável de 075. Este plano não autoriza alterar configurações das contas reais.

## Incrementos verificáveis

- [x] **84.1 — Atualizar matriz de suporte.** Confirmar fontes oficiais e pin de SDK de build; migrar somente Gateway/Tests/Benchmarks a net10.0-windows em fatia isolada. Node 24 LTS é alvo recomendado, com teste de 22 se o mínimo mantido for 22. Preservar Worker net48/x86, refs Artech e runtime empacotado. Não instalar ferramenta global nem trocar runtime da máquina.
- [x] **84.2 — Tornar dependências e pacote reproduzíveis.** CI root usa npm ci com lockfile; SDK e Actions ficam pinados com política de atualização. Manifest lista versão, commit, protocol revisions, nomes/tamanho/SHA dos artefatos, schema e Worker. Reusar zip+SHA existentes. SBOM/proveniência acrescentam verificação de origem; SHA sozinho prova integridade relativa ao manifest, não identidade do publicador.
- [x] **84.3 — Falhar fechado na distribuição moderna.** Instalador 3.x recusa checksum ausente/inválido e arquivos faltantes; compatibilidade com release antiga deve ser caminho explicitamente versionado, não qualquer falha de rede. Validar paths do arquivo ZIP antes da extração. Tests usam ZIPs sintéticos e mocks HTTP locais; não baixar release real para alterar a máquina.
- [x] **84.4 — Preparar antes de trocar.** Extrair em staging irmão, validar manifest, executar self-test com timeout e exit code verificados, e somente então trocar diretório com retenção do anterior. Preservar config.json/config raiz e autenticação. Falha de probe, extração, arquivo bloqueado ou disco cheio mantém instalação anterior operacional; rollback é testado em temp.
- [x] **84.5 — Sincronizar registradores e canais.** Reusar ClientConfigManager/CLI para preservar servidores terceiros, backups atômicos e ambos layouts OpenCode. Registro multi-conta real só após autorização; respeitar fonte canônica e valores por conta. Testar caminhos npx/global/fixed-path/VSIX em diretórios falsos; publicação deve exigir Gateway/Worker/schema/VSIX da mesma versão. Escopo integrado: registradores, backups, layouts OpenCode e artefatos do candidato foram verificados; publicação/signing e registro real multi-conta exigem autorização separada.
- [x] **84.6 — Ensaiar release sem publicar.** Revisar release.ps1 -DryRun e .github/workflows/release.yml, garantindo manifesto completo, ausência de segredos, checks presentes e orientação de restart total do cliente. Assinatura de código com certificado pago é item opcional de distribuição corporativa, sujeito a decisão/custo explícito; não bloquear correções locais esperando compra.

## Contratos de teste e oráculos

Fault matrix obrigatória no diretório temporário:
~~~text
checksum missing | checksum mismatch | zip traversal
zip incomplete | self-test exit 1 | self-test timeout
locked gateway | insufficient disk | interrupted extraction
third-party MCP preserved | both OpenCode layouts preserved
config old + application new -> failed probe -> old application intact
~~~
Testes PowerShell devem usar biblioteca/fakes existentes quando possível; se Pester não estiver declarado, usar assertions PowerShell no próprio script de contrato, sem instalação global.

## Comandos de verificação

Executar na raiz em PowerShell, com SDK instalado para Worker. Usar o lockfile para dependências já declaradas quando necessário; nenhuma instalação global. Comando de teste verde exige testes esperados executados, não só exit 0 com filtro vazio. Reproduzir falha em teste antes de corrigir comportamento.

~~~powershell
npm test
npm run lint
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj
pwsh -NoProfile -File scripts/tests/install-contract.tests.ps1
# Rehearsal somente após revisar comportamento DryRun e apontar saída descartável:
pwsh -NoProfile -File release.ps1 -Version 3.0.0 -DryRun
~~~

Resultado esperado: exit 0, todos os cenários selecionados executados e aprovados, nenhum skip obrigatório. Comandos de testes novos só existem após o incremento que os cria. Para Worker, configurar GX_PATH conforme a instalação local; GXMCP_TEST_KB deve apontar à fixture isolada de 074. Antes de iniciar servidor, verificar porta/health/processo. Artefatos transitórios em scratchpad/ ignorado; cleanup apenas dos recursos criados pelo teste.

## Critérios de conclusão

- [x] Gateway no runtime alvo passa os contratos antigos/novos; Worker mantém net48/x86.
- [x] Atualização moderna sem integridade comprovada falha antes de alterar instalação.
- [x] Faults cobertos pelos testes de staging preservam versão anterior e configurações.
- [x] Manifest, npm e VSIX do candidato representam o mesmo build; nada é publicado por este plano sem pedido explícito.
- [x] Revisão do diff por lógica, scope, falhas, concorrência, compatibilidade e integridade concluída.
- [x] Handoff entregue no relatório de integração com manifest, SBOM, installer e RC.
- [x] Integrador atualizou o estado no manifest; assinatura/publicação continuam sob autorização do mantenedor.

## Condições de parada

Dependência Gateway exigir API incompatível com .NET10; build/release DryRun mutar estado não descartável; assinatura exigir compra; instalador precisar apagar diretório desconhecido. Preparar diff e decisão concreta, sem rodar ação destrutiva.

Duas tentativas fracassadas sem evidência nova exigem coleta do dado que decide. Falha de ambiente é reportada como não validado, nunca como sucesso. Se uma próxima etapa implicar alteração irreversível, API pública não especificada ou efeito externo não autorizado, preparar o resultado revisável e solicitar a decisão necessária.

## Manutenção

Suporte .NET8 termina em 10/11/2026; .NET10 tem suporte até 14/11/2028 segundo Microsoft consultada em 05/09/2026. Node18/20 constam EOL. Revalidar datas ao iniciar; não confundir migração de Gateway com migração do SDK GeneXus.
