use crate::analysis::{CallersResult, ImpactResult, PackTaskResult, PathResult};
use crate::model::StatusResult;

fn sanitize_mermaid_id(s: &str) -> String {
    s.chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '_' { c } else { '_' })
        .collect()
}

pub fn format_path_mermaid(res: &PathResult) -> String {
    let mut out = String::new();
    out.push_str("```mermaid\n");
    out.push_str("graph LR\n");
    if !res.found || res.hops.is_empty() {
        let from_id = sanitize_mermaid_id(&res.from);
        let to_id = sanitize_mermaid_id(&res.to);
        out.push_str(&format!("  {}[\"{}\"]\n", from_id, res.from));
        out.push_str(&format!("  {}[\"{}\"]\n", to_id, res.to));
        out.push_str("  %% No path found between symbols\n");
    } else {
        for (idx, hop) in res.hops.iter().enumerate() {
            let from_id = format!("n_{}_{}", idx, sanitize_mermaid_id(&hop.from));
            let to_id = format!("n_{}_{}", idx + 1, sanitize_mermaid_id(&hop.to));
            let label = if hop.line > 0 {
                format!("{}:{}", hop.kind, hop.line)
            } else {
                hop.kind.clone()
            };
            out.push_str(&format!(
                "  {}[\"{}\"] -->|{}| {}[\"{}\"]\n",
                from_id, hop.from, label, to_id, hop.to
            ));
        }
    }
    out.push_str("```\n");
    out
}

pub fn format_callers_mermaid(res: &CallersResult) -> String {
    let mut out = String::new();
    out.push_str("```mermaid\n");
    out.push_str("graph TD\n");
    let target_id = format!("target_{}", sanitize_mermaid_id(&res.target));
    out.push_str(&format!("  {}[\"{}\"]\n", target_id, res.target));
    if res.callers.is_empty() {
        out.push_str("  %% 0 callers (root symbol or unreferenced)\n");
    } else {
        for (idx, caller) in res.callers.iter().enumerate() {
            let caller_id = format!("c{}_{}", idx, sanitize_mermaid_id(&caller.name));
            let label = if caller.line > 0 {
                format!("{}:{}", caller.call_mode, caller.line)
            } else {
                caller.call_mode.clone()
            };
            out.push_str(&format!(
                "  {}[\"{}\"] -->|{}| {}\n",
                caller_id, caller.name, label, target_id
            ));
        }
    }
    out.push_str("```\n");
    out
}

pub fn format_impact_mermaid(res: &ImpactResult) -> String {
    let mut out = String::new();
    out.push_str("```mermaid\n");
    out.push_str("graph LR\n");
    let root_id = format!("root_{}", sanitize_mermaid_id(&res.target));
    out.push_str(&format!("  {}[\"{}\"]\n", root_id, res.target));
    if res.blast_radius == 0 {
        out.push_str("  %% 0 impacted symbols (safe leaf or isolated)\n");
    } else {
        let mut idx = 0;
        for c in &res.callers {
            let cid = format!("c{}_{}", idx, sanitize_mermaid_id(&c.name));
            out.push_str(&format!("  {} -->|caller| {}[\"{}\"]\n", root_id, cid, c.name));
            idx += 1;
        }
        for w in &res.writers {
            let wid = format!("w{}_{}", idx, sanitize_mermaid_id(&w.name));
            out.push_str(&format!("  {} -->|writer| {}[\"{}\"]\n", root_id, wid, w.name));
            idx += 1;
        }
        for r in &res.readers {
            let rid = format!("r{}_{}", idx, sanitize_mermaid_id(&r.name));
            out.push_str(&format!("  {} -->|reader| {}[\"{}\"]\n", root_id, rid, r.name));
            idx += 1;
        }
    }
    out.push_str("```\n");
    out
}

pub fn format_status_mermaid(res: &StatusResult) -> String {
    let mut out = String::new();
    out.push_str("```mermaid\n");
    out.push_str("graph TD\n");
    out.push_str("  subgraph Architectural_Hubs[\"Architectural Hubs\"]\n");
    for (idx, hub) in res.hubs.iter().enumerate() {
        let hid = format!("hub_{}_{}", idx, sanitize_mermaid_id(&hub.name));
        let label = format!("{} ({} refs)", hub.name, hub.refs);
        match hub.kind.as_str() {
            "Tbl" | "Table" => out.push_str(&format!("    {}[(\"{}\")]\n", hid, label)),
            "Proc" | "Procedure" => out.push_str(&format!("    {}[[\"{}\"]]\n", hid, label)),
            _ => out.push_str(&format!("    {}[\"{}\"]\n", hid, label)),
        }
    }
    out.push_str("  end\n");
    out.push_str("```\n");
    out
}

pub fn format_pack_task_mermaid(res: &PackTaskResult) -> String {
    let mut out = String::new();
    out.push_str("```mermaid\n");
    out.push_str("graph LR\n");
    for (idx, anchor) in res.anchors.iter().enumerate() {
        let aid = format!("anchor_{}_{}", idx, sanitize_mermaid_id(&anchor.name));
        out.push_str(&format!("  {}[[\"{}\"]]\n", aid, anchor.name));
        for (cidx, caller_name) in anchor.callers.iter().enumerate() {
            let cid = format!("c_{}_{}_{}", idx, cidx, sanitize_mermaid_id(caller_name));
            out.push_str(&format!("  {}[\"{}\"] -->|caller| {}\n", cid, caller_name, aid));
        }
        for (cidx, callee_name) in anchor.callees.iter().enumerate() {
            let cid = format!("ce_{}_{}_{}", idx, cidx, sanitize_mermaid_id(callee_name));
            out.push_str(&format!("  {} -->|callee| {}[\"{}\"]\n", aid, cid, callee_name));
        }
    }
    out.push_str("```\n");
    out
}
