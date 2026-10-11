use crate::analysis::{CallersResult, ImpactResult, PackTaskResult, PathResult, SearchResult, SliceResult};
use crate::model::{DoctorResult, SafeDeleteResult, StatusResult};

pub fn format_status_xml(res: &StatusResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<status schema="{schema}" root="{root}" cache_exists="{exists}" nodes="{nodes}" edges="{edges}" empty="{empty}">"#,
        schema = res.schema,
        root = res.root,
        exists = res.cache_exists,
        nodes = res.nodes_count,
        edges = res.edges_count,
        empty = res.empty
    ));

    if !res.hubs.is_empty() {
        out.push_str(&format!(r#"<hubs count="{}">"#, res.hubs.len()));
        for h in &res.hubs {
            out.push_str(&format!(
                r#"<h n="{name}" t="{kind}" refs="{refs}" pr="{pr:.2}" />"#,
                name = h.name,
                kind = h.kind,
                refs = h.refs,
                pr = h.pagerank
            ));
        }
        out.push_str("</hubs>");
    }

    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</status>");
    out
}

pub fn format_doctor_xml(res: &DoctorResult) -> String {
    format!(
        r#"<doctor schema="{schema}" version="{version}" root="{root}" cache_exists="{exists}" cache_path="{path}" status="{status}"><next>{next}</next></doctor>"#,
        schema = res.schema,
        version = res.version,
        root = res.root,
        exists = res.cache_exists,
        path = res.cache_path,
        status = res.status,
        next = res.next
    )
}

pub fn format_safe_delete_xml(res: &SafeDeleteResult) -> String {
    format!(
        r#"<safe-delete schema="{schema}" symbol="{symbol}" can_delete="{can_del}" blast_radius="{blast}" callers="{callers}" empty="{empty}"><next>{next}</next></safe-delete>"#,
        schema = res.schema,
        symbol = res.symbol,
        can_del = res.can_delete,
        blast = res.blast_radius,
        callers = res.callers_count,
        empty = res.empty,
        next = res.next
    )
}

pub fn format_error_xml(code: &str, target: &str, message: &str, next: &str) -> String {
    format!(
        r#"<error code="{code}" target="{target}" message="{message}"><next>{next}</next></error>"#,
        code = code,
        target = target,
        message = message,
        next = next
    )
}

pub fn format_callers_xml(res: &CallersResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        "<!-- gxwire callers schema={schema}: 1-hop CALLERS of {target} (count={count}, transitive={trans_count}) -->\n",
        schema = res.schema,
        target = res.target,
        count = res.count,
        trans_count = res.transitive_count
    ));
    out.push_str(&format!(
        r#"<callers schema="{schema}" of="{target}" type="{kind}" count="{count}" transitive="{trans_count}" empty="{empty}" truncated="{trunc}" omitted="{omitted}">"#,
        schema = res.schema,
        target = res.target,
        kind = res.target_kind,
        count = res.count,
        trans_count = res.transitive_count,
        empty = res.empty,
        trunc = res.truncated,
        omitted = res.omitted
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

    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</callers>");
    out
}

pub fn format_impact_xml(res: &ImpactResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<impact schema="{schema}" of="{target}" type="{kind}" blast="{blast}" reorg_risk="{risk}" can_delete="{can_del}" est_tokens="{tokens}" empty="{empty}" truncated="{trunc}" omitted="{omitted}">"#,
        schema = res.schema,
        target = res.target,
        kind = res.target_kind,
        blast = res.blast_radius,
        risk = res.reorg_risk,
        can_del = res.can_delete,
        tokens = res.est_tokens,
        empty = res.empty,
        trunc = res.truncated,
        omitted = res.omitted
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

    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</impact>");
    out
}

pub fn format_search_xml(res: &SearchResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<ctx task="{query}" schema="{schema}" count="{count}" total="{total}" est_tokens="{tokens}" empty="{empty}" truncated="{trunc}" omitted="{omitted}">"#,
        query = res.query,
        schema = res.schema,
        count = res.count,
        total = res.total,
        tokens = res.est_tokens,
        empty = res.empty,
        trunc = res.truncated,
        omitted = res.omitted
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
    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</ctx>");
    out
}

pub fn format_pack_task_xml(res: &PackTaskResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<pack task="{task}" budget="{budget}" est_tokens="{tokens}" schema="{schema}" empty="{empty}" truncated="{trunc}" omitted="{omitted}">"#,
        task = res.task,
        budget = res.budget,
        tokens = res.est_tokens,
        schema = res.schema,
        empty = res.empty,
        trunc = res.truncated,
        omitted = res.omitted
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

    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</pack>");
    out
}

pub fn format_slice_xml(res: &SliceResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<slice schema="{schema}" obj="{obj}" var="{var}" defs="{defs}" uses="{uses}" points="{points}" empty="{empty}">"#,
        schema = res.schema,
        obj = res.object,
        var = res.variable,
        defs = res.definitions_count,
        uses = res.uses_count,
        points = res.points_count,
        empty = res.empty
    ));

    for p in &res.points {
        out.push_str(&format!(
            r#"<p l="{line}" k="{kind}"><![CDATA[{code}]]></p>"#,
            line = p.line,
            kind = p.kind,
            code = p.code_snippet
        ));
    }

    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</slice>");
    out
}

pub fn format_path_xml(res: &PathResult) -> String {
    let mut out = String::new();
    out.push_str(&format!(
        r#"<path schema="{schema}" from="{from}" to="{to}" found="{found}" hops="{hops}" empty="{empty}">"#,
        schema = res.schema,
        from = res.from,
        to = res.to,
        found = res.found,
        hops = res.hops_count,
        empty = res.empty
    ));
    for h in &res.hops {
        out.push_str(&format!(
            r#"<hop from="{from}" to="{to}" kind="{kind}" line="{line}" />"#,
            from = h.from,
            to = h.to,
            kind = h.kind,
            line = h.line
        ));
    }
    out.push_str(&format!("<next>{}</next>", res.next));
    out.push_str("</path>");
    out
}

