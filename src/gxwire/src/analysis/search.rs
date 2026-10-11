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
    let needle_bytes = needle.as_bytes();
    let n_len = needle_bytes.len();
    if n_len == 0 {
        return true;
    }
    let h_bytes = haystack.as_bytes();
    if h_bytes.len() < n_len {
        return false;
    }
    h_bytes.windows(n_len).any(|window| window.eq_ignore_ascii_case(needle_bytes))
}

#[inline]
fn equals_case_insensitive(a: &str, b: &str) -> bool {
    a.as_bytes().eq_ignore_ascii_case(b.as_bytes())
}

static GX_SYNONYM_GROUPS: &[&[&str]] = &[
    &["fat", "fatura", "faturamento", "faturar", "invoice", "billing", "invoicing"],
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
            if group.iter().any(|syn| syn.eq_ignore_ascii_case(&term_lower) || term_lower.starts_with(syn)) {
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

pub fn search_for_task_ext(graph: &CsrGraph, query: &str, limit: usize, full: bool) -> SearchResult {
    let query_trimmed = query.trim();
    let terms: Vec<&str> = query_trimmed.split_whitespace().collect();
    let synonyms = expand_synonyms(&terms);

    let mut scored_nodes = Vec::new();

    for node in &graph.nodes {
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
            if let Some(ref desc) = node.description {
                if contains_case_insensitive(desc, term) {
                    score += 5.0;
                }
            }
            if let Some(ref module) = node.module {
                if contains_case_insensitive(module, term) {
                    score += 3.0;
                }
            }
        }

        // Domain synonym matching
        for syn in &synonyms {
            if contains_case_insensitive(&node.name, syn) {
                score += 25.0;
            }
            let node_lower = node.name.to_ascii_lowercase();
            if node_lower.starts_with(&format!("p{}", syn))
                || node_lower.starts_with(&format!("trn{}", syn))
                || node_lower.starts_with(&format!("t{}", syn))
                || node_lower.starts_with(&format!("wp{}", syn))
                || node_lower.starts_with(&format!("w{}", syn))
            {
                score += 35.0;
            }
        }

        if score > 0.0 {
            // Apply PageRank boost
            let pr = graph.pagerank.get(node.id as usize).copied().unwrap_or(1.0);
            let callers_count = graph.get_callers(node.id).len();
            let total_score = score * (1.0 + pr * 5.0) + (callers_count as f32 * 0.5);

            scored_nodes.push((node, total_score, callers_count));
        }
    }

    scored_nodes.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

    let total = scored_nodes.len();
    let take_count = if full { total } else { limit };
    let hits: Vec<SearchHit> = scored_nodes
        .into_iter()
        .take(take_count)
        .enumerate()
        .map(|(idx, (node, score, callers_count))| SearchHit {
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
        format!("gxwire --path {} --to <Target> or gxwire --callers {} or gxwire --pack-task \"{}\"", hits[0].name, hits[0].name, hits[0].name)
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
