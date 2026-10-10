use crate::analysis::{CallersResult, ImpactResult, PackTaskResult, SearchResult, SliceResult};

pub fn format_callers_xml(res: &CallersResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        "<!-- gxwire callers schema=gxwire.callers/v1: 1-hop CALLERS of {target} (count={count}, transitive={trans_count}) -->\n",
        target = res.target,
        count = res.count,
        trans_count = res.transitive_count
    ));
    out.push_str(&format!(
        r#"<callers schema="gxwire.callers/v1" of="{target}" type="{kind}" count="{count}" transitive="{trans_count}">"#,
        target = res.target,
        kind = res.target_kind,
        count = res.count,
        trans_count = res.transitive_count
    ));

    for c in &res.callers {
        out.push_str(&format!(
            r#"<c n="{name}" t="{kind}" mode="{mode}" l="{line}" />"#,
            name = c.name,
            kind = c.kind,
            mode = c.call_mode,
            line = c.line
        ));
    }

    if !res.transitive.is_empty() {
        out.push_str(r#"<transitive>"#);
        for t in &res.transitive {
            out.push_str(&format!(
                r#"<tc n="{name}" t="{kind}" d="{depth}" />"#,
                name = t.name,
                kind = t.kind,
                depth = t.depth
            ));
        }
        out.push_str(r#"</transitive>"#);
    }

    out.push_str("</callers>");
    out
}

pub fn format_impact_xml(res: &ImpactResult) -> String {
    let mut out = String::new();
    let est_tokens = 60 + res.writers.len() * 12 + res.readers.len() * 10 + res.formulas.len() * 15 + res.callers.len() * 10;

    out.push_str(&format!(
        r#"<impact schema="gxwire.impact/v1" of="{target}" type="{kind}" blast="{blast}" reorg_risk="{risk}" est_tokens="{tokens}">"#,
        target = res.target,
        kind = res.target_kind,
        blast = res.blast_radius,
        risk = res.reorg_risk,
        tokens = est_tokens
    ));

    if !res.writers.is_empty() {
        out.push_str(&format!(r#"<writers count="{}">"#, res.writers.len()));
        for w in &res.writers {
            out.push_str(&format!(
                r#"<w n="{name}" t="{kind}" rel="{rel}" l="{line}" />"#,
                name = w.name,
                kind = w.kind,
                rel = w.relationship,
                line = w.line
            ));
        }
        out.push_str("</writers>");
    }

    if !res.readers.is_empty() {
        out.push_str(&format!(r#"<readers count="{}">"#, res.readers.len()));
        for r in &res.readers {
            out.push_str(&format!(
                r#"<r n="{name}" t="{kind}" rel="{rel}" l="{line}" />"#,
                name = r.name,
                kind = r.kind,
                rel = r.relationship,
                line = r.line
            ));
        }
        out.push_str("</readers>");
    }

    if !res.formulas.is_empty() {
        out.push_str(&format!(r#"<formulas count="{}">"#, res.formulas.len()));
        for f in &res.formulas {
            out.push_str(&format!(
                r#"<f n="{name}" t="{kind}" />"#,
                name = f.name,
                kind = f.kind
            ));
        }
        out.push_str("</formulas>");
    }

    if !res.callers.is_empty() {
        out.push_str(&format!(r#"<callers count="{}">"#, res.callers.len()));
        for c in &res.callers {
            out.push_str(&format!(
                r#"<c n="{name}" t="{kind}" d="{depth}" />"#,
                name = c.name,
                kind = c.kind,
                depth = c.depth
            ));
        }
        out.push_str("</callers>");
    }

    if !res.tests.is_empty() {
        out.push_str(r#"<tests>"#);
        for t in &res.tests {
            out.push_str(&format!(r#"<t n="{}" status="ready" />"#, t));
        }
        out.push_str("</tests>");
    }

    out.push_str(&format!(r#"<next>--slice={target}:VAR</next>"#, target = res.target));
    out.push_str("</impact>");
    out
}

pub fn format_search_xml(res: &SearchResult) -> String {
    let mut out = String::new();
    let est_tokens = 50 + res.hits.len() * 25;

    out.push_str(&format!(
        r#"<ctx task="{query}" schema="gxwire.for/v1" count="{count}" total="{total}" est_tokens="{tokens}">"#,
        query = res.query,
        count = res.hits.len(),
        total = res.total,
        tokens = est_tokens
    ));

    out.push_str("<sigs>");
    for hit in &res.hits {
        let parm_attr = hit
            .parm_rule
            .as_ref()
            .map(|p| format!(r#" parm="{}""#, p))
            .unwrap_or_default();

        out.push_str(&format!(
            r#"<d r="{rank}" n="{name}" t="{kind}" cx="{cx}" in="{callers}" score="{score:.1}"{parm}>{desc}</d>"#,
            rank = hit.rank,
            name = hit.name,
            kind = hit.kind,
            cx = hit.complexity,
            callers = hit.callers_count,
            score = hit.score,
            parm = parm_attr,
            desc = hit.description.as_deref().unwrap_or("")
        ));
    }
    out.push_str("</sigs>");
    out.push_str("</ctx>");
    out
}

pub fn format_pack_task_xml(res: &PackTaskResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<pack task="{task}" budget="{budget}" est_tokens="{tokens}" schema="gxwire.pack/v1">"#,
        task = res.task,
        budget = res.budget,
        tokens = res.est_tokens
    ));

    out.push_str("<anchors>");
    for a in &res.anchors {
        let parm_attr = a
            .parm_rule
            .as_ref()
            .map(|p| format!(r#" parm="{}""#, p))
            .unwrap_or_default();

        out.push_str(&format!(
            r#"<a r="{rank}" n="{name}" t="{kind}"{parm}>"#,
            rank = a.rank,
            name = a.name,
            kind = a.kind,
            parm = parm_attr
        ));

        if !a.callers.is_empty() {
            out.push_str(&format!(r#"<in>{}</in>"#, a.callers.join(",")));
        }
        if !a.callees.is_empty() {
            out.push_str(&format!(r#"<out>{}</out>"#, a.callees.join(",")));
        }

        out.push_str("</a>");
    }
    out.push_str("</anchors>");

    if !res.related_tests.is_empty() {
        out.push_str(&format!(r#"<tests>{}</tests>"#, res.related_tests.join(",")));
    }

    out.push_str("</pack>");
    out
}

pub fn format_slice_xml(res: &SliceResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<slice obj="{obj}" var="{var}" defs="{defs}" uses="{uses}">"#,
        obj = res.object,
        var = res.variable,
        defs = res.definitions_count,
        uses = res.uses_count
    ));

    for p in &res.points {
        out.push_str(&format!(
            r#"<p l="{line}" k="{kind}"><![CDATA[{code}]]></p>"#,
            line = p.line,
            kind = p.kind,
            code = p.code_snippet
        ));
    }

    out.push_str("</slice>");
    out
}
