# Snapshot canônico no salvamento isolado de Events

Uma edição idempotente de Events com `requireObjectSave=true` podia terminar em
`ObjectSaveIncomplete`: Rules aparecia vazia antes do Save e preenchida depois,
embora a leitura independente já retornasse a mesma fonte nos dois momentos.

No SDK U16, a serialização nativa de uma parte de fonte pode consumir um campo
interno ainda não materializado. O snapshot agora lê `ISource.Source` antes de
serializar. Não chama o setter nem salva Rules. A comparação usa fonte ou XML
canônico; a restauração mantém os bytes nativos quando disponíveis.

A prévia captura e compara duas instâncias SDK pelo mesmo capturador do Save.
O Save exclui somente Events da comparação de partes, usando explicitamente a
sobrecarga de lista. Rules, Variables, WebForm, Pattern, propriedades e o
inventário dos demais objetos continuam protegidos antes do commit e na
releitura posterior. Uma mudança real em parte preservada impede o commit.

O recibo expõe formatos, comprimentos e hashes das partes preservadas antes e
depois, sem incluir suas fontes. `partPersisted` descreve a comparação da parte;
`persisted` exige também commit. `transactionCommitted` e `idempotent` tornam o
resultado inequívoco. `isolationTimings` separa o custo do Save dos três
inventários. Um timeout continua exigindo consultar a operação já enviada;
não se deve repetir o Save.

O caminho já auditado de Save isolado reconhece também o conjunto coerente
U16 `18.0.16.189595`, mantendo o conjunto anterior e recusando mistura de seus
componentes. Isso não modifica a compatibilidade geral por major do Worker.
Os aliases de concorrência `baseVersion` e `expectedVersion` usam a mesma
barreira; valores conflitantes são recusados antes do despacho.

A regressão usa uma parte sintética com fonte materializada sob demanda e
serializador que consome o cache. Reverter a ordem reproduz a fonte vazia e
faz o teste falhar. O teste também distingue uma mudança substantiva em Rules.

Na distribuição operacional com esta correção, a validação nativa U16 incluiu
WebPanel sintético com Rules não vazias, prévia sem escrita, Save real, Save
idempotente, token obsoleto e uma alteração inesperada de Rules injetada apenas
no pacote privado de ensaio, com rollback verificado. Um WebPanel com host
WorkWithPlus também concluiu Save idempotente e releitura independente das
partes preservadas. Essa evidência corresponde aos cenários executados e não
substitui novas validações para SDKs ou callbacks ainda não auditados.

## Bundle U16 com WorkWithPlus 16.2

O bundle `18.0.16.189595+wwp16.2.0.8386` tem um manifesto versionado em
`config/events-u16-wwp16.2-audit.json`, incorporado no Worker e distribuído como
arquivo idêntico. São 452 arquivos do SDK e WWP, incluindo recursos de idioma.
Todas as nove DLLs centrais são obrigatórias. Arquivo ausente, hash diferente,
mistura de builds ou implementação SDK carregada fora do conjunto recusam o
Save isolado. A compatibilidade geral por major permanece independente.

O ensaio de 14/10/2026 usou o Worker nativo e um WebPanel sintético com
instância WWP `Empty` persistida. Prévia sem mutação, Save real, Save
idempotente, conflito antes de escrita, releituras de todas as partes e
metadados dos demais objetos foram comprovados. Uma alteração de Rules foi
injetada somente no pacote privado de ensaio e no nome sintético autorizado:
impediu o commit, provocou rollback atômico e deixou fonte, revisão e token
iguais ao estado anterior. O objeto e sua instância foram removidos ao final.

Os Saves longos atingiram `WorkerTimeout`. Consultou-se a mesma operação por
`genexus_lifecycle(action='status', target='op:...')` e, ao chegar ao estado
terminal, por `action='result'`. O recibo completo fica em `workerPayload` na
consulta de resultado; status não precisa conter esse corpo. Nenhum Save foi
reenviado. O timeout síncrono não altera o resultado terminal do worker.

As APIs de isolamento continuam `EventsSuspended` nos brokers global e da KB
e a flag transitória `SkipApplyPattern`, restauradas ao sair. A inspeção do
SDK confirmou o teste dessa flag em `Package.OnAfterSave` antes de aplicar
Pattern. Os vinte subscribers WWP identificados usam o broker; hooks de
documento da UI não integram o Save nativo. Corpos protegidos do SDK exigiram
as provas nativas: leitura de IL não foi apresentada como prova exclusiva.
Não se altera `SDPlus_Editor_Apply_On_Save` para False, nem se chama Specify,
Generate, Build, Rebuild, reorg, deploy ou execução de aplicação.

Durante a auditoria, a build candidata permitia o bundle novo apenas no
objeto sintético exato. A autorização final consta do manifesto incorporado
somente depois das provas. Alterar o JSON externo não muda a autorização.

## Bundle U16 build 189647 com WorkWithPlus 16.2

A instalação GeneXus 18 U16 passou da build 189595 para a 189647 com o mesmo
WorkWithPlus 16.2.0.8386 (os quatro DLLs DVelop críticos têm os mesmos bytes).
Cada build recompila todas as assemblies: 426 dos 452 arquivos do bundle
auditado mudaram, o manifesto da 189595 deixou de conferir e o Save isolado
passou a recusar com `SdkBundleAuditFailed` em toda KB U16 dessa build. A
build é auditada como um bundle próprio, `config/events-u16-wwp16.2-189647-audit.json`
(452 arquivos, as mesmas nove DLLs centrais). Como os dois bundles U16 carregam
o mesmo WorkWithPlus, o registro escolhe o manifesto pelos bytes exatos do
WorkWithPlus e, entre os que o carregam, pelos bytes exatos de
`Artech.Architecture.Common`; um núcleo que nenhum bundle auditou não seleciona
nada e a recusa nomeia os bundles auditados. A exigência de hash exato de todos
os arquivos do manifesto e de todas as assemblies carregadas não mudou.

A auditoria sem KB usou como controle o próprio bundle auditado da 189595, refeito a
partir do arquivo de instalação do fabricante daquela build (extraído numa pasta
temporária e completado com os arquivos do pacote WorkWithPlus instalado): os 452
arquivos do seu manifesto conferem com os hashes (444 vêm do arquivo de instalação;
os outros 8 são do WorkWithPlus, que ele não traz e cujos hashes são os mesmos em
todos os manifestos). Foram lidas 49 assemblies
com o leitor de IL de 14/10 (variante que lê todos os tipos; 164.846 métodos): as
nove centrais, as 17 de implementação de padrões e controles de usuário e mais 23
que guardam assinantes de tópicos de Save (30 das 223 assemblies de código assinam
algum tópico de Save). Os 236 ganchos de Save são idênticos em IL e em
referências, inclusive `Package.OnAfterSave`, que testa `SkipApplyPattern` antes de
aplicar um padrão, e as 524 assinaturas `EventSubscription` das 223 assemblies de
código dos manifestos, lidas dos metadados sem carregar nada, são as mesmas nos
mesmos tópicos, assembly por assembly. Das 35 diferenças de IL, 33 são construtores
estáticos e ajudantes do ofuscador (só a chave do decifrador de strings, que muda a
cada build) e 2 são métodos de Design System de `Artech.Genexus.Common`; nenhuma
tem relação com o Save de Events e nenhum gancho de Save referencia diretamente
um método cujo IL difere.

A prova nativa de 13/11/2026 usou o pipeline do U11 e do U10 numa KB descartável
com uma instância de padrão WorkWithPlus, com o manifesto `pending` e dois
WebPanels sintéticos removidos ao final: prévia sem mutação, outro WebPanel
recusado enquanto pendente, token obsoleto recusado, Save assíncrono com recibo
completo (revisão avança exatamente 1, demais partes e metadados dos outros
objetos intactos, nenhuma operação implícita), releitura fresca, reinício do
Worker, Save idempotente, erro de sintaxe com rollback e fila com uma vencedora e
uma perdedora `VersionConflict`: 88 verificações, nenhuma falhou. O resultado
sanitizado está em `docs/evidence/events-u16-wwp16.2-189647-native-proof.json` e
seu SHA-256 é o `proofEvidenceSha256` do manifesto. Não coberto: salvar um objeto
que é, ele próprio, host WorkWithPlus persistido (a KB descartável não tem Design
System, então não se cria host nela); a prova de 14/10 cobriu esse caso na 189595
e os ganchos e os bytes do WorkWithPlus são os mesmos. O manifesto registra isso
em `nativeVerification.notCovered`. Detalhes em
[Required Events save isolation](events-object-save-isolation.md#genexus-18-u16-build-189647).

## Demais builds do GeneXus 18 U16 (189158, 189513 e 189552)

O U16 saiu como versão oficial (build 189158) e em builds posteriores; o mesmo
WorkWithPlus 16.2.0.8386 está instalado em todas, byte a byte, e cada build é um
bundle próprio com manifesto próprio (`config/events-u16-wwp16.2-189158-audit.json`,
`-189513-` e `-189552-`, além dos da 189595 e da 189647): 452 arquivos e as mesmas
nove DLLs centrais. O registro escolhe o manifesto pelos bytes exatos de
`Artech.Architecture.Common`; uma build sem manifesto próprio segue recusada
(`SdkBundleAuditFailed`, nomeando os bundles auditados com aquele WorkWithPlus).
Nada foi relaxado: todo arquivo do manifesto precisa conferir com o SHA-256 e toda
assembly SDK carregada precisa estar listada.

Cada manifesto foi gerado a partir do arquivo de instalação do fabricante da build,
extraído numa pasta temporária fora da instalação e completado com os arquivos do
WorkWithPlus instalado; nada foi instalado nem alterado. A auditoria sem KB repetiu
a da 189647 (mesmas 49 assemblies, mesmo leitor, mesmo controle): os 236 ganchos de
Save são idênticos em IL e em referências nas três builds, as 524 assinaturas
`EventSubscription` das 223 assemblies de código são as mesmas, assembly por
assembly, e nenhum gancho referencia diretamente um método cujo IL difere. Métodos
com IL diferente contra o controle: 33 na 189552 (só construtores estáticos e
ajudantes do ofuscador), 35 na 189513 (os mesmos e 2 do lançador da aplicação
gerada, `RunDotNetCore`) e 104 na 189158 (os mesmos 33 e 71 fora do caminho do Save:
contêiner de controles de usuário, geração de serviços REST, TeamDev, implantação).

A prova nativa de 13/11/2026 foi a da 189647 rodada uma vez por build, com o Worker
carregando o SDK da cópia extraída daquela build: 88 verificações em três conjuntos,
nenhuma falhou nas três (na 189513 a 1ª rodada falhou uma verificação do cenário de
concorrência, a primeira de duas gravações enfileiradas parou na captura de linha de
base com `ObjectSaveIncomplete`, sem gravar nada; a 2ª rodada passou as 88). Os
resultados sanitizados estão em
`docs/evidence/events-u16-wwp16.2-189158-native-proof.json`, `-189513-` e `-189552-`,
e o SHA-256 de cada um é o `proofEvidenceSha256` do respectivo manifesto. Não coberto:
salvar um objeto que é, ele próprio, host WorkWithPlus persistido. Detalhes em
[Required Events save isolation](events-object-save-isolation.md#the-other-genexus-18-u16-builds).

## Bundle U11 com WorkWithPlus 16.1

O bundle `18.0.11.54344+wwp16.1.2.8167` segue o mesmo mecanismo, com o manifesto
`config/events-u11-wwp16.1-audit.json` (459 arquivos). Sua prova nativa, de
28/10/2026, vale para uma KB sem nenhuma instância de padrão
(`nativeVerification.scope = kb-without-pattern-instances`): o Worker conta os
`PatternInstance` do modelo inteiro depois de verificar o bundle e recusa o Save
como `PatternInstancesNotVerified` se houver qualquer um ou se a contagem falhar.
O ensaio salvou um WebPanel sintético pelo pipeline protegido (prévia sem mutação,
token obsoleto recusado, Save assíncrono com recibo completo, releitura fresca,
reinício do Worker, Save idempotente, erro de sintaxe com rollback verificado e
fila com uma vencedora e uma perdedora `VersionConflict`) e removeu os objetos
sintéticos ao final. O resultado sanitizado está em
`docs/evidence/events-u11-wwp16.1-native-proof.json`; o SHA-256 desse arquivo é o
`proofEvidenceSha256` do manifesto. Não houve ensaio com host WorkWithPlus
persistido. Detalhes em [Required Events save isolation](events-object-save-isolation.md).
