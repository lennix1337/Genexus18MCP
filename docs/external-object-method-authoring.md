# Autoria de métodos de External Object

`genexus_authoring action=add_external_method` acrescenta um método pelo SDK, sem substituir os métodos, propriedades, eventos ou tipos genéricos existentes. O tipo do EO, datastore e documentação do objeto permanecem sob o controle das propriedades atuais.

```json
{
  "action": "add_external_method",
  "name": "DatabaseProcedures",
  "payload": {
    "name": "UpdateUsage",
    "externalName": "Sample.UpdateUsage",
    "dryRun": true,
    "parameters": [
      {"name":"Id","type":"GUID","inout":"in","externalName":"Id"},
      {"name":"Active","type":"Boolean","inout":"in","externalName":"Active"},
      {"name":"Object","type":"VarChar","length":128,"inout":"in","externalName":"Object"},
      {"name":"Success","type":"Boolean","inout":"out","externalName":"Success"},
      {"name":"Message","type":"VarChar(512)","inout":"out","externalName":"Message"}
    ]
  }
}
```

Após revisar a prévia, enviar `dryRun=false` e o `versionToken` como `payload.expectedVersion`. A versão é opcional para compatibilidade com clientes antigos; use-a para detectar alterações concorrentes. `direction` é alias de `inout`; direções conflitantes são recusadas. `length` e `decimals` também aceitam a notação de tamanho no tipo. Tipos externos antigos continuam aceitos, e `externalType` permite explicitar o mapeamento. `description` é opcional no método e nos parâmetros.

Repetir o mesmo nome e contrato devolve `ExternalMethodAlreadyExists` sem Save. Um contrato diferente com o mesmo nome devolve `ExternalMethodConflict`. Parâmetros inválidos ou duplicados falham antes de alterar a coleção. A gravação cria snapshot nativo local, salva e relê o objeto por identidade. Só devolve `ExternalMethodAdded` após comparar integralmente o contrato relido, incluindo os membros anteriores; falha tenta remover exclusivamente a adição e informa `rollbackVerified`.

`genexus_read part=EXOStructure` (ou `Structure`/`Source`) expõe `externalMethods`, ordem, tipo, tamanho, decimais, direção, nomes externos, referências e propriedades nativas. A leitura completa fornece `externalStructureVersion`; inspect com `include=["structure"]` também fornece o contrato. A representação textual é uma projeção JSON fiel, exportável como parte/Object Text; não é uma entrada para `import_part`. O importador genérico é recusado para EXOStructure porque sua desserialização não transporta as coleções.

Confirme a persistência também por XPZ oficial após salvar. Uma prévia de importação é somente inspeção. Quando `ImportFile` retorna `false`, `genexus_transfer` devolve `status=error`, `TransferImportDeclined`, `success=false`, `imported=false` e `sdkDiagnostics`. `diagnosticsAvailable=false` indica ausência do serviço de saída; uma lista vazia não autoriza inventar uma causa. Exceções deixam a conclusão da importação indeterminada e exigem conferir os objetos persistidos antes de repetir.
