# Guia de início — GeneXus MCP

Este guia te leva do zero até "o assistente de IA está editando minha KB do GeneXus" em uns 5-10 minutos.

> Para problemas durante ou depois da instalação, consulte [TROUBLESHOOTING.md](../TROUBLESHOOTING.md).

---

## O que é isso, em uma frase?

É uma ponte entre seu **assistente de IA** (Claude, Cursor, Antigravity, etc.) e uma **Knowledge Base de um major suportado do GeneXus**. Uma vez instalado, você pode pedir pra IA coisas como *"adiciona uma regra na transação Pedido que valide o total"* e a IA usa o SDK nativo do GeneXus pra fazer isso de verdade na sua KB.

---

## Pré-requisitos

Antes de começar, garanta que você tem:

- ✅ **Windows** (GeneXus é só Windows)
- ✅ **Um SDK suportado do GeneXus** instalado (consulte os caminhos em [`docs/generated/supported-versions.md`](generated/supported-versions.md))
- ✅ **Uma KB criada com um major suportado do GeneXus** que você já tenha aberto pelo menos uma vez no IDE (pra que esteja inicializada)
- ✅ **Node.js 22 ou superior** — verifica com `node --version` num terminal; baixa em [nodejs.org](https://nodejs.org/) se não tiver
- ✅ **Um cliente de IA compatível com MCP** — [Claude Desktop](https://claude.ai/download), [Claude Code](https://claude.com/claude-code), Cursor, Antigravity, etc.

**Não** precisa clonar o repositório. **Não** precisa instalar nada globalmente com `npm`. Tudo é gerenciado pelo `npx`.

**Nunca usou um terminal?** Aperta `Win+R`, digita `powershell`, Enter. Esse é seu terminal.

---

## Passo 1 — Identifique seus dois caminhos

Antes de rodar o instalador, anote:

1. **Caminho de instalação do GeneXus** — a pasta onde está o `GeneXus.exe`.
   Exemplo: `C:\Program Files (x86)\GeneXus\GeneXus18`

2. **Caminho da sua KB** — a pasta raiz da sua Knowledge Base (a que contém o arquivo `.gx` e pastas como `Model/`, `WebSpa/`).
   Exemplo: `C:\KBs\MinhaKnowledgeBase`

O major do SDK precisa ser o mesmo major usado para criar a KB. Para uma KB do
GeneXus 17, use a instalação `GeneXus17Trial`; para uma KB do GeneXus 18, use
`GeneXus18`. O instalador valida essa combinação antes de gravar a configuração.

Se não tem certeza do caminho da KB, abra ela no GeneXus e veja na barra de título ou no menu File → Recent.

---

## Passo 2 — Rodar o instalador

Abra um **terminal novo** (PowerShell ou CMD) e cole esse comando, **trocando os caminhos pelos seus**:

```bash
npx genexus-mcp@latest init --kb "C:\KBs\MinhaKnowledgeBase" --gx "C:\Program Files (x86)\GeneXus\GeneXus18"
```

Exemplo para uma KB do GeneXus 17:

```bash
npx genexus-mcp@latest init --kb "C:\KBs\KBTeste17" --gx "C:\Program Files (x86)\GeneXus\GeneXus17Trial"
```

O que você vai ver:

1. `npx` baixa o pacote (na primeira vez demora 10-30 segundos).
2. O instalador verifica que os caminhos existem e que o GeneXus está presente.
3. **Detecta automaticamente** quais clientes de IA você tem instalados (Claude Desktop, Claude Code, Cursor, Antigravity) e adiciona a configuração do MCP em cada um.
4. Imprime um bloco JSON no final — guarde pra caso precise configurar manualmente algum cliente que não foi detectado.
5. Termina com `🎉 You are all set!`.

> **Prefere o assistente interativo?** Rode `npx genexus-mcp@latest init --interactive` e responda as perguntas.

---

## Passo 3 — Reiniciar o cliente de IA

Isso é importante: **feche completamente** seu cliente de IA e abra de novo.

- **Claude Desktop**: clique direito no ícone da bandeja do sistema → Quit. Depois abra novamente. (Fechar a janela **não basta**.)
- **Claude Code**: feche a sessão e abra de novo.
- **Cursor / Antigravity**: feche todas as janelas e reabra.

O cliente precisa reiniciar pra descobrir o novo MCP server.

---

## Passo 4 — Testar que funciona

No seu cliente de IA, cole esse prompt:

> *"Usando o GeneXus MCP, lista os 5 primeiros objetos da minha KB e me mostra o nome e tipo de cada um."*

**O que deveria acontecer?**

- A IA deveria invocar a ferramenta `genexus_list_objects` (às vezes você vê na UI como "calling tool…").
- Em alguns segundos, te retorna uma lista de objetos da sua KB.

Se isso funcionou, **terminou!** Pode pular pra próxima seção e ver o que mais dá pra fazer.

Se **não** ver a lista ou a IA disser que não tem a ferramenta do GeneXus, vai pro [Troubleshooting](../TROUBLESHOOTING.md).

---

## Seus primeiros prompts úteis

Depois que o "list 5 objects" funcionou, teste esses:

### Explorar a KB

> *"Me mostra o código fonte do procedure CalcularTotalFatura."*

> *"Busca todas as transações que usam o atributo IdCliente."*

> *"Quais objetos eu tenho do tipo WebPanel?"*

### Analisar código

> *"Me explica passo a passo o que o procedure ProcessarEnvio faz."*

> *"Qual SQL é gerado pela query do WebPanel ListaClientes?"*

> *"Me resume a estrutura do módulo Vendas."*

### Editar a KB

> *"Adiciona na transação Pedido uma regra que dispara um erro se Total for menor que 0."*

> *"Cria uma nova transação chamada Categoria com os atributos IdCategoria, NomeCategoria e Ativa."*

> *"No procedure CriarPedido, renomeia a variável &qtd pra &quantidade."*

#### Comentar uma instrução com segurança

Para desativar uma instrução no `Source`, leia primeiro a parte e reutilize o
`versionToken` como `baseVersion`. Em `mode="patch"`, a substituição
`msg(...)` → `//msg(...)` é tratada como alteração somente de comentário:

- `dryRun=true` mostra o diff e não salva nem altera o token;
- a gravação exige `baseVersion` para evitar sobrescrever uma edição concorrente;
- o MCP salva uma vez, invalida os caches, relê a parte pelo SDK e confirma o
  comentário solicitado;
- a resposta separa `saved` de `verified`, inclui os hashes solicitado e relido
  e informa `implicitOperations: []`;
- `saved: true` só aparece quando a releitura completa confirma o conteúdo;
  `saveAttempted` informa separadamente que o SDK recebeu uma tentativa de
  gravação;
- se a releitura divergir, retorna `CommentOnlyWriteNotPersisted`; com
  `rollbackOnFailure=true`, restaura e verifica o snapshot anterior.

Esse fluxo não executa implicitamente Specify, Generate, Build, Rebuild,
compilação, reorganização, execução nem testes da KB.

```json
{
  "name": "MeuProcedure",
  "type": "Procedure",
  "part": "Source",
  "mode": "patch",
  "operation": "Replace",
  "context": "msg(&Guid.ToString(),nowait)",
  "content": "//msg(&Guid.ToString(),nowait)",
  "expectedCount": 1,
  "baseVersion": "<versionToken da leitura>",
  "dryRun": false,
  "verifyMode": "exact",
  "rollbackOnFailure": true
}
```

### Editar telas WorkWithPlus (patterns)

O MCP edita o XML completo de `PatternInstance` / `PatternVirtual`: adicionar, remover e reordenar controles (textBlock, atributos, botões, grupos, ordens, filtros), aplicar classes de tema (`themeClass`, `buttonClass`, `groupThemeClass`), e reorganizar as visões **Transaction** e **Selection** de forma independente.

> *"No WorkWithPlusPedido, adiciona um botão 'Duplicar' na visão de transação ao lado de Guardar/Cancelar/Eliminar."*

> *"Agrupa os atributos da transação Cliente numa seção 'Dados de contato' com groupThemeClass='GroupTelaResp'."*

> *"Na lista do WorkWithPlusFatura, adiciona uma ordenação nova por DataFatura descendente."*

> *"Aplica buttonClass='btn ButtonGreen' no botão Guardar do WorkWithPlusPedido e bota o header do formulário com themeClass='BigTitle'."*

> *"Remove a ação Exportar da grade de Selection no WorkWithPlusRelatorio."*

> *"Lê a parte Documentation da transação Cliente e reescreve em markdown."*

O MCP recalcula automaticamente o atributo `childrenOrderedList` que o IDE usa pra renderizar a ordem — você só diz *onde* vai o elemento no XML, o MCP cuida pra que apareça lá no IDE. A resposta inclui um bloco `childrenOrderedListReconciliation` com o que foi reescrito e por quê.

Pra descobrir as classes de tema disponíveis na sua KB:

> *"Lista todos os ThemeClass cujo nome contenha 'Button'."*

(Por baixo dos panos isso chama `genexus_list_objects --typeFilter ThemeClass --nameFilter Button`.)

**Detalhe chave**: os botões custom (Duplicar, Auditar, Exportar…) precisam ser `<userAction>`, não `<standardAction>`. Só `Trn_Enter` / `Trn_Cancel` / `Trn_Delete` são ações padrão registradas em uma transaction WorkWithPlus. O reconciler trata `<userAction>` como par de `<standardAction>` e os dois convivem na mesma linha de TableActions.

Se você quer que seus overrides de pattern sobrevivam ao engine do WorkWithPlus (que normaliza alguns campos como o `title` em cada save), desliga o "Apply on save" assim:

> *"Seta a property SDPlus_Editor_Apply_On_Save do WorkWithPlusPedido pra False."*

Internamente isso é `genexus_properties --action set --name WorkWithPlus<Objeto> --propertyName SDPlus_Editor_Apply_On_Save --value False` (aceita `True | False | Default`).

Detalhes completos do workflow, a matriz de capacidades verificadas e orientações sobre o pattern-engine do WorkWithPlus: ver [README — WorkWithPlus pattern editing](../README.md#workwithplus-pattern-editing--what-you-can-actually-do).

### Build e testing

> *"Compila a KB e me reporta os erros se tiver algum."*

> *"Roda os testes unitários e me mostra quais falharam."*

---

## Dicas pra tirar proveito

**1. Seja específico com os nomes dos objetos.** A IA é boa pesquisando, mas se você falar *"edita a transação de pedidos"* ela pode perguntar qual (Pedido, PedidoDetalhe, OrdemPedido…). Diga o nome exato quando souber.

**2. Peça `dryRun` quando estiver testando.** Pra edições, você pode pedir: *"faz isso em modo dryRun primeiro e me mostra o preview"*. O MCP devolve o que ia mudar **sem tocar na KB**. Útil quando você está aprendendo ou quando a mudança é delicada.

**3. A primeira chamada depois de um tempo é lenta.** O "worker" (a parte que fala com o GeneXus) desliga depois de 5 minutos sem uso pra não travar arquivos. A primeira chamada depois disso demora 3-8 segundos pra iniciar. É por design, não é bug.

**4. Se você for buildar a KB pelo IDE do GeneXus, pare o worker antes usando a tool MCP:**

```bash
genexus_worker_reload mode=soft
```

Senão pode dar conflito de arquivos bloqueados.

**5. Veja a lista completa de ferramentas:**

```bash
npx genexus-mcp tools list
```

Te mostra os 30+ tools que a IA tem disponível. Você não precisa decorar — a IA escolhe sozinha — mas ajuda saber o que é possível.

---

## Algo não está funcionando?

1. Rode o diagnóstico:
   ```bash
   npx genexus-mcp doctor --mcp-smoke
   ```
2. Procure seu problema no [TROUBLESHOOTING.md](../TROUBLESHOOTING.md).
3. Se nada disso funcionar, [abra um issue](https://github.com/lennix1337/Genexus18MCP/issues) com a saída do comando anterior e os logs do seu cliente de IA.

---

## O que vem agora?

- **Lista completa de ferramentas e modos de edição**: [README principal](../README.md#tool-surface)
- **Contrato CLI para agentes (AXI)**: [`docs/axi_cli_contract.md`](axi_cli_contract.md)
- **Playbook de boas práticas LLM**: [`docs/llm_cli_mcp_playbook.md`](llm_cli_mcp_playbook.md)
- **Configuração avançada** (timeouts, networking, shadow paths): [README principal — Advanced Configuration](../README.md#advanced-configuration)

Bem-vindo ao projeto! Se você encontrar bugs ou tiver ideias, **abrir issues é a forma mais útil de contribuir** — fica registrado e ajuda os próximos usuários.
