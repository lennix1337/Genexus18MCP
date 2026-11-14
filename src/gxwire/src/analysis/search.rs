use crate::graph::CsrGraph;
use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct SearchHit {
    pub name: String,
    pub kind: String,
    pub rank: usize,
    pub score: f32,
    pub complexity: u32,
    pub callers_count: usize,
    pub parm_rule: Option<String>,
    pub description: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct SearchResult {
    pub schema: String,
    pub query: String,
    pub hits: Vec<SearchHit>,
    pub total: usize,
    pub count: usize,
    pub empty: bool,
    pub truncated: bool,
    pub omitted: usize,
    pub est_tokens: usize,
    pub next: String,
}

#[inline]
fn contains_case_insensitive(haystack: &str, needle: &str) -> bool {
    let needle = needle.as_bytes();
    let Some((&first, rest)) = needle.split_first() else {
        return true;
    };
    let haystack = haystack.as_bytes();
    if haystack.len() < needle.len() {
        return false;
    }
    let first = first.to_ascii_lowercase();
    let last_start = haystack.len() - needle.len();
    let mut i = 0;
    while i <= last_start {
        if haystack[i].to_ascii_lowercase() == first
            && haystack[i + 1..i + needle.len()].eq_ignore_ascii_case(rest)
        {
            return true;
        }
        i += 1;
    }
    false
}

#[inline]
fn equals_case_insensitive(a: &str, b: &str) -> bool {
    a.as_bytes().eq_ignore_ascii_case(b.as_bytes())
}

static GX_SYNONYM_GROUPS: &[&[&str]] = &[
    &[
        "fat",
        "fatura",
        "faturamento",
        "faturar",
        "invoice",
        "billing",
        "invoicing",
    ],
    &["ped", "pedido", "pedidos", "order", "orders"],
    &["cli", "cliente", "clientes", "customer", "client"],
    &["forn", "fornecedor", "fornecedores", "supplier", "vendor"],
    &["nf", "nfe", "notafiscal", "danfe", "fiscal"],
    &["tit", "titulo", "titulos", "receivable", "payable", "bill"],
    &["rec", "receber", "contasareceber", "receipt"],
    &["pag", "pagar", "contasapagar", "payment"],
    &["prc", "proc", "procedimento", "procedure"],
    &["trn", "trans", "transacao", "transaction"],
    &["wp", "webpanel", "painel", "panel"],
    &["prod", "produto", "produtos", "item", "product"],
    &["est", "estoque", "stock", "inventory"],
    &["ven", "venda", "vendas", "sale", "sales"],
    &["com", "compra", "compras", "purchase"],
    &["cta", "conta", "contas", "contabil", "account"],
    &["rel", "relatorio", "relatorios", "report"],
    &["mov", "movimento", "movimentacao", "movement", "ledger"],
    &["imp", "imposto", "impostos", "tax", "tributo"],
    &["bco", "banco", "bancos", "bank"],
    &["usu", "usuario", "usuarios", "user", "login"],
    &["val", "validar", "validacao", "validate"],
    &["calc", "calcular", "calculo", "calculate"],
    &["ger", "gerar", "geracao", "generate"],
    &["int", "integracao", "integrar", "integration"],
];

fn expand_synonyms(terms: &[&str]) -> Vec<&'static str> {
    let mut expanded = Vec::new();
    for term in terms {
        let term_lower = term.to_ascii_lowercase();
        for group in GX_SYNONYM_GROUPS {
            if group
                .iter()
                .any(|syn| syn.eq_ignore_ascii_case(&term_lower) || term_lower.starts_with(syn))
            {
                for syn in *group {
                    if !syn.eq_ignore_ascii_case(&term_lower) && !expanded.contains(syn) {
                        expanded.push(*syn);
                    }
                }
            }
        }
    }
    expanded
}

#[allow(dead_code)]
pub fn search_for_task(graph: &CsrGraph, query: &str, limit: usize) -> SearchResult {
    search_for_task_ext(graph, query, limit, false)
}

pub fn search_for_task_ext(
    graph: &CsrGraph,
    query: &str,
    limit: usize,
    full: bool,
) -> SearchResult {
    let query_trimmed = query.trim();
    let terms: Vec<&str> = query_trimmed.split_whitespace().collect();
    let synonyms = expand_synonyms(&terms);
    let synonym_prefixes: Vec<(&str, [String; 5])> = synonyms
        .iter()
        .map(|syn| {
            (
                *syn,
                ["p", "trn", "t", "wp", "w"].map(|kind| format!("{kind}{syn}")),
            )
        })
        .collect();

    let mut scored_nodes: Vec<(usize, &crate::model::EntityNode, f32, usize)> = Vec::new();

    for (order, node) in graph.nodes.iter().enumerate() {
        let mut score = 0.0f32;

        // Exact & substring match
        if equals_case_insensitive(&node.name, query_trimmed) {
            score += 100.0;
        } else if contains_case_insensitive(&node.name, query_trimmed) {
            score += 40.0;
        }

        // Term-based matching
        for term in &terms {
            if contains_case_insensitive(&node.name, term) {
                score += 15.0;
            }
            if let Some(ref desc) = node.description
                && contains_case_insensitive(desc, term)
            {
                score += 5.0;
            }
            if let Some(ref module) = node.module
                && contains_case_insensitive(module, term)
            {
                score += 3.0;
            }
        }

        // Domain synonym matching
        if !synonyms.is_empty() {
            let node_lower = node.name.to_ascii_lowercase();
            for (syn, prefixes) in &synonym_prefixes {
                if contains_case_insensitive(&node.name, syn) {
                    score += 25.0;
                }
                if prefixes
                    .iter()
                    .any(|prefix| node_lower.starts_with(prefix.as_str()))
                {
                    score += 35.0;
                }
            }
        }

        if score > 0.0 {
            // Apply PageRank boost
            let pr = graph.pagerank.get(node.id as usize).copied().unwrap_or(1.0);
            let callers_count = graph.caller_count(node.id);
            let total_score = score * (1.0 + pr * 5.0) + (callers_count as f32 * 0.5);

            scored_nodes.push((order, node, total_score, callers_count));
        }
    }

    let total = scored_nodes.len();
    let take_count = if full { total } else { limit };
    // Highest score first; equal scores keep graph order, as the former stable sort did.
    // Only the hits that are returned get sorted.
    let by_score = |a: &(usize, &crate::model::EntityNode, f32, usize),
                    b: &(usize, &crate::model::EntityNode, f32, usize)| {
        b.2.partial_cmp(&a.2)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then(a.0.cmp(&b.0))
    };
    if take_count < total {
        if take_count > 0 {
            scored_nodes.select_nth_unstable_by(take_count - 1, by_score);
        }
        scored_nodes.truncate(take_count);
    }
    scored_nodes.sort_unstable_by(by_score);
    let hits: Vec<SearchHit> = scored_nodes
        .into_iter()
        .take(take_count)
        .enumerate()
        .map(|(idx, (_, node, score, callers_count))| SearchHit {
            name: node.name.clone(),
            kind: node.kind.as_str().to_string(),
            rank: idx + 1,
            score,
            complexity: node.complexity,
            callers_count,
            parm_rule: node.parm_rule.clone(),
            description: node.description.clone(),
        })
        .collect();

    let count = hits.len();
    let empty = count == 0;
    let (truncated, omitted) = if !full && total > count {
        (true, total - count)
    } else {
        (false, 0)
    };
    let est_tokens = 50 + count * 25;
    let next = if empty {
        "gxwire --doctor or check query spelling".to_string()
    } else {
        format!(
            "gxwire --path {} --to <Target> or gxwire --callers {} or gxwire --pack-task \"{}\"",
            hits[0].name, hits[0].name, hits[0].name
        )
    };

    SearchResult {
        schema: "gxwire.for/v1".to_string(),
        query: query.to_string(),
        hits,
        total,
        count,
        empty,
        truncated,
        omitted,
        est_tokens,
        next,
    }
}
