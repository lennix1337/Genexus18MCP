use crate::analysis::{CallersResult, ImpactResult, PackTaskResult, PathResult, SearchResult, SliceResult};
use crate::model::{DoctorResult, SafeDeleteResult, StatusResult};

pub fn escape_toon_scalar(val: &str) -> String {
    let clean = val.replace('\r', "").replace('\n', "\\n");
    if !clean.is_empty()
        && clean.chars().all(|c| {
            c.is_ascii_alphanumeric()
                || c == '.'
                || c == '_'
                || c == ':'
                || c == '/'
                || c == '\\'
                || c == '-'
        })
    {
        clean
    } else {
        format!("\"{}\"", clean.replace('"', "\\\""))
    }
}

pub fn format_status_toon(res: &StatusResult) -> String {
    let mut out = String::new();
    out.push_str("cache_exists: ");
    out.push_str(&res.cache_exists.to_string());
    out.push('\n');
    out.push_str("cache_path: ");
    out.push_str(&escape_toon_scalar(&res.cache_path));
    out.push('\n');
    out.push_str("edges_count: ");
    out.push_str(&res.edges_count.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("nodes_count: ");
    out.push_str(&res.nodes_count.to_string());
    out.push('\n');
    out.push_str("root: ");
    out.push_str(&escape_toon_scalar(&res.root));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');

    if res.hubs.is_empty() {
        out.push_str("top_hubs: []\n");
    } else {
        out.push_str(&format!("top_hubs:\n  [{}]{{kind,name,pagerank,refs}}:\n", res.hubs.len()));
        for h in &res.hubs {
            out.push_str(&format!(
                "    {},{},{:.2},{}\n",
                escape_toon_scalar(&h.kind),
                escape_toon_scalar(&h.name),
                h.pagerank,
                h.refs
            ));
        }
    }
    out
}

pub fn format_doctor_toon(res: &DoctorResult) -> String {
    let mut out = String::new();
    out.push_str("cache_exists: ");
    out.push_str(&res.cache_exists.to_string());
    out.push('\n');
    out.push_str("cache_path: ");
    out.push_str(&escape_toon_scalar(&res.cache_path));
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("root: ");
    out.push_str(&escape_toon_scalar(&res.root));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("status: ");
    out.push_str(&escape_toon_scalar(&res.status));
    out.push('\n');
    out.push_str("version: ");
    out.push_str(&escape_toon_scalar(&res.version));
    out.push('\n');
    out
}

pub fn format_callers_toon(res: &CallersResult) -> String {
    let mut out = String::new();
    out.push_str("count: ");
    out.push_str(&res.count.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("omitted: ");
    out.push_str(&res.omitted.to_string());
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("target: ");
    out.push_str(&escape_toon_scalar(&res.target));
    out.push('\n');
    out.push_str("target_kind: ");
    out.push_str(&escape_toon_scalar(&res.target_kind));
    out.push('\n');
    out.push_str("transitive_count: ");
    out.push_str(&res.transitive_count.to_string());
    out.push('\n');
    out.push_str("truncated: ");
    out.push_str(&res.truncated.to_string());
    out.push('\n');

    if res.callers.is_empty() {
        out.push_str("callers: []\n");
    } else {
        out.push_str(&format!("callers:\n  [{}]{{call_mode,depth,kind,line,name}}:\n", res.callers.len()));
        for c in &res.callers {
            out.push_str(&format!(
                "    {},{},{},{},{}\n",
                escape_toon_scalar(&c.call_mode),
                c.depth,
                escape_toon_scalar(&c.kind),
                c.line,
                escape_toon_scalar(&c.name)
            ));
        }
    }

    if res.transitive.is_empty() {
        out.push_str("transitive: []\n");
    } else {
        out.push_str(&format!("transitive:\n  [{}]{{depth,kind,name}}:\n", res.transitive.len()));
        for t in &res.transitive {
            out.push_str(&format!(
                "    {},{},{}\n",
                t.depth,
                escape_toon_scalar(&t.kind),
                escape_toon_scalar(&t.name)
            ));
        }
    }
    out
}

pub fn format_impact_toon(res: &ImpactResult) -> String {
    let mut out = String::new();
    out.push_str("blast_radius: ");
    out.push_str(&res.blast_radius.to_string());
    out.push('\n');
    out.push_str("can_delete: ");
    out.push_str(&res.can_delete.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("est_tokens: ");
    out.push_str(&res.est_tokens.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("omitted: ");
    out.push_str(&res.omitted.to_string());
    out.push('\n');
    out.push_str("reorg_risk: ");
    out.push_str(&escape_toon_scalar(&res.reorg_risk));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("target: ");
    out.push_str(&escape_toon_scalar(&res.target));
    out.push('\n');
    out.push_str("target_kind: ");
    out.push_str(&escape_toon_scalar(&res.target_kind));
    out.push('\n');
    out.push_str("truncated: ");
    out.push_str(&res.truncated.to_string());
    out.push('\n');

    if res.writers.is_empty() {
        out.push_str("writers: []\n");
    } else {
        out.push_str(&format!("writers:\n  [{}]{{depth,kind,line,name,relationship}}:\n", res.writers.len()));
        for w in &res.writers {
            out.push_str(&format!(
                "    {},{},{},{},{}\n",
                w.depth,
                escape_toon_scalar(&w.kind),
                w.line,
                escape_toon_scalar(&w.name),
                escape_toon_scalar(&w.relationship)
            ));
        }
    }

    if res.readers.is_empty() {
        out.push_str("readers: []\n");
    } else {
        out.push_str(&format!("readers:\n  [{}]{{depth,kind,line,name,relationship}}:\n", res.readers.len()));
        for r in &res.readers {
            out.push_str(&format!(
                "    {},{},{},{},{}\n",
                r.depth,
                escape_toon_scalar(&r.kind),
                r.line,
                escape_toon_scalar(&r.name),
                escape_toon_scalar(&r.relationship)
            ));
        }
    }

    if res.formulas.is_empty() {
        out.push_str("formulas: []\n");
    } else {
        out.push_str(&format!("formulas:\n  [{}]{{kind,name}}:\n", res.formulas.len()));
        for f in &res.formulas {
            out.push_str(&format!(
                "    {},{}\n",
                escape_toon_scalar(&f.kind),
                escape_toon_scalar(&f.name)
            ));
        }
    }

    if res.callers.is_empty() {
        out.push_str("callers: []\n");
    } else {
        out.push_str(&format!("callers:\n  [{}]{{depth,kind,name}}:\n", res.callers.len()));
        for c in &res.callers {
            out.push_str(&format!(
                "    {},{},{}\n",
                c.depth,
                escape_toon_scalar(&c.kind),
                escape_toon_scalar(&c.name)
            ));
        }
    }

    if res.tests.is_empty() {
        out.push_str("tests: []\n");
    } else {
        out.push_str(&format!("tests:\n  [{}]:\n", res.tests.len()));
        for t in &res.tests {
            out.push_str(&format!("    - {}\n", escape_toon_scalar(t)));
        }
    }

    out
}

pub fn format_search_toon(res: &SearchResult) -> String {
    let mut out = String::new();
    out.push_str("count: ");
    out.push_str(&res.count.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("est_tokens: ");
    out.push_str(&res.est_tokens.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("omitted: ");
    out.push_str(&res.omitted.to_string());
    out.push('\n');
    out.push_str("query: ");
    out.push_str(&escape_toon_scalar(&res.query));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("total: ");
    out.push_str(&res.total.to_string());
    out.push('\n');
    out.push_str("truncated: ");
    out.push_str(&res.truncated.to_string());
    out.push('\n');

    if res.hits.is_empty() {
        out.push_str("hits: []\n");
    } else {
        out.push_str(&format!("hits:\n  [{}]{{callers,complexity,kind,name,parm,rank,score}}:\n", res.hits.len()));
        for h in &res.hits {
            out.push_str(&format!(
                "    {},{},{},{},{},{},{:.1}\n",
                h.callers_count,
                h.complexity,
                escape_toon_scalar(&h.kind),
                escape_toon_scalar(&h.name),
                escape_toon_scalar(h.parm_rule.as_deref().unwrap_or("")),
                h.rank,
                h.score
            ));
        }
    }
    out
}

pub fn format_pack_task_toon(res: &PackTaskResult) -> String {
    let mut out = String::new();
    out.push_str("budget: ");
    out.push_str(&res.budget.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("est_tokens: ");
    out.push_str(&res.est_tokens.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("omitted: ");
    out.push_str(&res.omitted.to_string());
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("task: ");
    out.push_str(&escape_toon_scalar(&res.task));
    out.push('\n');
    out.push_str("truncated: ");
    out.push_str(&res.truncated.to_string());
    out.push('\n');

    if res.anchors.is_empty() {
        out.push_str("anchors: []\n");
    } else {
        out.push_str(&format!("anchors:\n  [{}]{{callees,callers,kind,name,parm,rank}}:\n", res.anchors.len()));
        for a in &res.anchors {
            let callers_str = a.callers.join(",");
            let callees_str = a.callees.join(",");
            out.push_str(&format!(
                "    {},{},{},{},{},{}\n",
                escape_toon_scalar(&callees_str),
                escape_toon_scalar(&callers_str),
                escape_toon_scalar(&a.kind),
                escape_toon_scalar(&a.name),
                escape_toon_scalar(a.parm_rule.as_deref().unwrap_or("")),
                a.rank
            ));
        }
    }

    if res.related_tests.is_empty() {
        out.push_str("related_tests: []\n");
    } else {
        out.push_str(&format!("related_tests:\n  [{}]:\n", res.related_tests.len()));
        for t in &res.related_tests {
            out.push_str(&format!("    - {}\n", escape_toon_scalar(t)));
        }
    }
    out
}

pub fn format_slice_toon(res: &SliceResult) -> String {
    let mut out = String::new();
    out.push_str("definitions_count: ");
    out.push_str(&res.definitions_count.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("object: ");
    out.push_str(&escape_toon_scalar(&res.object));
    out.push('\n');
    out.push_str("points_count: ");
    out.push_str(&res.points_count.to_string());
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("uses_count: ");
    out.push_str(&res.uses_count.to_string());
    out.push('\n');
    out.push_str("variable: ");
    out.push_str(&escape_toon_scalar(&res.variable));
    out.push('\n');

    if res.points.is_empty() {
        out.push_str("points: []\n");
    } else {
        out.push_str(&format!("points:\n  [{}]{{kind,line,snippet}}:\n", res.points.len()));
        for p in &res.points {
            out.push_str(&format!(
                "    {},{},{}\n",
                escape_toon_scalar(&p.kind),
                p.line,
                escape_toon_scalar(&p.code_snippet)
            ));
        }
    }
    out
}

pub fn format_safe_delete_toon(res: &SafeDeleteResult) -> String {
    let mut out = String::new();
    out.push_str("blast_radius: ");
    out.push_str(&res.blast_radius.to_string());
    out.push('\n');
    out.push_str("callers_count: ");
    out.push_str(&res.callers_count.to_string());
    out.push('\n');
    out.push_str("can_delete: ");
    out.push_str(&res.can_delete.to_string());
    out.push('\n');
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("symbol: ");
    out.push_str(&escape_toon_scalar(&res.symbol));
    out.push('\n');
    out
}

pub fn format_error_toon(code: &str, target: &str, message: &str, next: &str) -> String {
    let mut out = String::new();
    out.push_str("error:\n");
    out.push_str(&format!("  code: {}\n", escape_toon_scalar(code)));
    out.push_str(&format!("  message: {}\n", escape_toon_scalar(message)));
    if !target.is_empty() {
        out.push_str(&format!("  target: {}\n", escape_toon_scalar(target)));
    }
    out.push_str(&format!("next: {}\n", escape_toon_scalar(next)));
    out
}

pub fn format_path_toon(res: &PathResult) -> String {
    let mut out = String::new();
    out.push_str("empty: ");
    out.push_str(&res.empty.to_string());
    out.push('\n');
    out.push_str("est_tokens: ");
    out.push_str(&res.est_tokens.to_string());
    out.push('\n');
    out.push_str("found: ");
    out.push_str(&res.found.to_string());
    out.push('\n');
    out.push_str("from: ");
    out.push_str(&escape_toon_scalar(&res.from));
    out.push('\n');
    out.push_str("hops_count: ");
    out.push_str(&res.hops_count.to_string());
    out.push('\n');
    out.push_str("next: ");
    out.push_str(&escape_toon_scalar(&res.next));
    out.push('\n');
    out.push_str("schema: ");
    out.push_str(&escape_toon_scalar(&res.schema));
    out.push('\n');
    out.push_str("to: ");
    out.push_str(&escape_toon_scalar(&res.to));
    out.push('\n');

    if res.hops.is_empty() {
        out.push_str("hops: []\n");
    } else {
        out.push_str(&format!("hops:\n  [{}]{{from,kind,line,to}}:\n", res.hops.len()));
        for h in &res.hops {
            out.push_str(&format!(
                "    {},{},{},{}\n",
                escape_toon_scalar(&h.from),
                escape_toon_scalar(&h.kind),
                h.line,
                escape_toon_scalar(&h.to)
            ));
        }
    }
    out
}

