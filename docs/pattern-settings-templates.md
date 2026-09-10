# Templates de Pattern Settings: estado da correcao

## Estado

Implementacao parcial, sem promocao operacional. Resolucao tipada, leitura SDK
e planejamento de uma propriedade foram implementados. **Salvamento isolado
nao esta implementado nem certificado**: a tentativa retorna
`SettingsIsolationUnverified` antes de atribuir propriedades ou chamar Save.
O bloqueio tambem impede gravacao de Pattern Settings pelo editor generico.

A mudanca foi integrada sobre a versao 3.2.2. Os testes sao executados sem
abrir KB; dados sinteticos permitem validar o contrato de leitura e previa,
mas nao certificam persistencia nem efeitos dos eventos de salvamento.

## Causas encontradas

1. `ResolveTypedObjectDirect` podia converter um tipo sem GUID mapeado em
   pesquisa sem filtro e aceitar o primeiro homonimo. A resolucao agora valida
   o tipo retornado, normaliza Pattern Settings/PatternSettings e recusa
   identidades explicitas conflitantes. O cache de ObjectReader tambem nao
   distinguia tipo, leitura completa e conjunto de partes.
2. `PatternSettingsPart` tem armazenamento especializado. O serializador
   generico de propriedades nao representa sua arvore. A leitura agora usa
   `PatternSettings.PatternPart.RootElement.ToXml(..., Internal)`; nao usa
   `PatternSettings.Get(model, definition)`, que pode produzir defaults em
   memoria para um Settings ausente.
3. `ToXmlDocument()` produz Legacy, mas a API inversa nao aceita Legacy.
   Alem disso, `ReadFrom` pode executar adaptadores/update-process. A previa
   trabalha sobre uma projecao JSON separada, sem desserializar ou modificar
   o objeto SDK.
4. O SDK tem aplicacao automatica no evento AfterSave. O WorkWithPlus possui
   handlers proprios BeforeSave/AfterSave com ramos para PatternSettings.
   `SkipApplyPattern` nao demonstra isolamento desses handlers. Os corpos
   protegidos/obfuscados tambem impediram comprovar o contrato atomico de
   concorrencia entre processos. Ver [auditoria SDK](pattern-settings-sdk-audit.md).

## Contrato implementado

- `genexus_read` com `part: PatternSettings` retorna XML Internal, offset,
  totalLines, truncated e suggestedNextOffset. `limit: 0` pede a parte toda.
  Leituras por `parts` preservam o envelope, sem esconder truncamento.
- `genexus_wwp settings_templates` enumera elementos SDK dos tipos
  InstanceTemplate, WPInstanceTemplate e RPTemplate. Esses tipos foram
  conferidos na especificacao instalada; a enumeracao usa o Settings
  persistido, nunca o arquivo de defaults da instalacao.
- `settings_read` seleciona um template por caminho do catalogo, nome interno
  unico ou caption SDK. Retorna todos os nos, inclusive propriedades efetivas
  obtidas pelo SDK e elegibilidade para edicao. O label da interface pode
  diferir do caption do SDK; nao e usado para inventar identificadores.
- `settings_edit dryRun:true` calcula uma unica diferenca com antes/depois.
  Nao valida conversao do valor nem executa validadores de salvamento SDK;
  isso e declarado na resposta. Propriedades desconhecidas, nao aplicaveis,
  internas, somente leitura, chaves, Default*, ChildrenOrderedList e
  identificadores sao recusados.
- O token SHA-256 cobre identidade da KB/modelo/Settings, XML completo e
  propriedades efetivas de todos os nos. Alteracao em outro template tambem
  invalida a previa. Ele e token de snapshot SDK, **nao uma garantia de CAS
  no banco**. Nao se afirma que `Objects.Get` invalide caches internos do SDK.
- `settings_edit dryRun:false` exige token e recusa salvar. Nenhuma chamada
  de atualizacao, aplicacao, build, specify, generate, reorg, importacao,
  publicacao, GAM ou banco faz parte desse caminho.

## Exemplos de chamadas

Exemplos para o candidato. No Hub o prefixo e `native_`.
O alias de KB vem do perfil ja validado.
Os caminhos abaixo sao placeholders: copiar os valores efetivamente retornados.

Resolver o Settings homonimo e inspecionar suas partes:

```javascript
native_genexus_inspect({name: "WorkWithPlus", type: "Pattern Settings"})
native_genexus_read({name: "WorkWithPlus", type: "Pattern Settings",
  part: "PatternSettings", offset: 0, limit: 12})
```

Para XML completo, repetir a leitura com `limit: 0`. XML paginado e um fragmento;
concatenar as paginas, na ordem, antes de interpreta-lo como documento XML.

Listar templates, selecionar Transaction e ler os nos:

```javascript
native_genexus_wwp({action: "settings_templates", name: "WorkWithPlus", limit: 50})
native_genexus_wwp({action: "settings_read", name: "WorkWithPlus",
  template: "Transaction", limit: 50})
native_genexus_wwp({action: "settings_read", name: "WorkWithPlus",
  template: "<templatePath>", offset: 50, limit: 50, baseVersion: "<snapshot>"})
```

Continuar ate `truncated:false`, usando `nextOffset` retornado e o mesmo
token de `settings_templates/settings_read`. Em VersionConflict, descartar as
paginas anteriores e recomecar. `limit:0` retorna todos os nos. Tokens da
leitura generica de XML e da API de templates sao distintos; usar na edicao
o token de `settings_read` ou do dryRun.

Localizar o no cuja propriedade de nome identifica TableMain dentro do template
selecionado. Usar seu path e a grafia exata da propriedade de classe do SDK:

```javascript
native_genexus_wwp({action: "settings_edit", name: "WorkWithPlus",
  template: "<templatePath>", nodePath: "<TableMainPath>",
  property: "themeClass", value: "TableMainTransaction example-entry",
  baseVersion: "<snapshot>", dryRun: true})
```

Tentativa de salvamento isolado e verificacao:

```javascript
native_genexus_wwp({action: "settings_edit", name: "WorkWithPlus",
  template: "<templatePath>", nodePath: "<TableMainPath>",
  property: "themeClass", value: "TableMainTransaction example-entry",
  baseVersion: "<snapshot>", dryRun: false})
// Resultado esperado nesta entrega: SettingsIsolationUnverified, saved:false.
native_genexus_wwp({action: "settings_read", name: "WorkWithPlus",
  template: "<templatePath>", limit: 0})
// A releitura deve continuar mostrando o valor anterior. Nao e teste de persistencia.
```

## Arquivos

- Worker: ObjectService.cs, ObjectReader.cs, PatternSettingsService.cs,
  WwpActionService.cs, WriteService.cs e referencia SDK no csproj.
- Gateway: tool_definitions.json, OperationClassifier.cs, ToolHelpCatalog.cs
  e exclusao do cache semantico em Program.ToolPayload.cs.
- Testes: ObjectResolutionPriorityTests.cs, ObjectReaderTests.cs,
  PatternSettingsServiceTests.cs, testes de cache Gateway e fixture tools/list.
- Documentacao: CHANGELOG.md, inventario de capacidades, este relatorio e
  auditoria dos eventos SDK/WorkWithPlus.

## Validacoes executadas

- Suite Worker U16: 2.476 aprovados, quatro ignorados, zero falhas.
- Suite Gateway: 1.567 aprovados, 13 ignorados, zero falhas.
- Worker U11 e U12: build sem erros/avisos e 58 testes dirigidos aprovados
  em cada instalacao, com saidas isoladas por SDK.
- Testes dirigidos de resolucao/cache: 44 Worker e 34 Gateway aprovados.
- CLI: 110 aprovados; lint e validadores de metadados/contratos aprovados.
- `git diff --check`: aprovado.

O build Worker padrao da base 3.2.2 exige o fingerprint exato U10 do manifesto
`config/sdk-compatibility.json`; ele recusa a instalacao U16 utilizada nesta
auditoria. Os testes offline U16 usam `-p:GxMcpSkipSdkValidation=true`, sem
alterar esse manifesto. Isso valida compilacao e testes sinteticos contra o
SDK instalado, **nao** certifica o fingerprint de distribuicao nem autoriza
promocao. A versao do produto U16 inspecionada e 18.0.16.189550; as versoes
dos arquivos constam na auditoria.

Os testes de snapshot cobrem diferenca unica, imutabilidade da entrada,
propriedades protegidas, limite do template, token concorrente, identidade,
leitura de todas as paginas e recusa de Save. Os testes de resolucao cobrem
aliases de tipo, identidade textual e cache. Esses testes usam dados
sinteticos; nao demonstram persistencia em repositorio SDK.

## Pendencias ou validacoes nao executadas

1. Confirmar catalogo completo, Transaction/Default data e TableMain na KB alvo
   usando o candidato em ambiente autorizado, sem promover para operacao.
2. Obter contrato de isolamento de todos os handlers de Save e contrato de
   concorrencia atomica SDK. Nao remover o bloqueio por simples ausencia de
   chamadas explicitas de Apply.
3. Em KB descartavel autorizada, testar duas sessoes concorrentes; persistir
   uma propriedade e reler por SDK. Capturar antes/depois de todos os objetos,
   dependentes e instancias, incluindo eventos e operacoes proibidas.
4. Provar que somente a propriedade selecionada foi persistida e que nao
   houve atualizacao/aplicacao, lifecycle ou efeito fora do Settings.
5. Validar o fingerprint SDK exigido para distribuicao. Nenhuma classe de
   template operacional foi alterada por esta entrega.
