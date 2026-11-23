# Custo do `tools/list` por perfil (issue #1091)

Mede só uma parcela do consumo de tokens: o que o cliente carrega ao abrir a sessão, antes de qualquer tarefa. Não mede o custo por tarefa (busca, leitura, edição), nem o espelho local da KB.

Fonte: os fixtures `tools-list.<perfil>.response.json` (verificados pelos testes do Gateway, então não se afastam do servidor). Conta `name`, `description` e `inputSchema`, que é o que o modelo vê. Tokens estimados em 4 bytes por token; o valor real vem do cliente ou do provedor.

Reproduzir: `node scripts/bench/tools-list-size.js`.

| Perfil | Ferramentas | Bytes | Tokens (est.) |
|---|---:|---:|---:|
| core | 15 | 22.384 | ~5.600 |
| db | 19 | 29.973 | ~7.500 |
| standard | 20 | 37.402 | ~9.350 |
| devops | 37 | 44.268 | ~11.050 |
| ui | 23 | 45.645 | ~11.400 |
| authoring | 35 | 65.248 | ~16.300 |
| full | 60 | 97.254 | ~24.300 |

`adaptive` abre com as 15 ferramentas de `core` mais a meta ferramenta `genexus_tools`, montada em código (não há fixture): custo inicial um pouco acima de `core`, cerca de 23% do perfil completo.

## O que isto não responde

- Se ler o espelho em arquivos gasta menos tokens por tarefa concluída do que `genexus_query`/`genexus_read`. Isso exige repetir as mesmas tarefas numa KB descartável, com o mesmo modelo e cliente, e ler o consumo no cliente.
- Duração e tamanho da exportação inicial, atraso do espelho, cobertura de WebForms/WorkWithPlus, objetos homônimos e edição concorrente.
