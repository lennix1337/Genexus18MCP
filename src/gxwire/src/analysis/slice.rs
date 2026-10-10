use serde::Serialize;

#[derive(Debug, Clone, Serialize)]
pub struct SlicePoint {
    pub line: u32,
    pub kind: String, // "def", "use", "parm"
    pub code_snippet: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct SliceResult {
    pub object: String,
    pub variable: String,
    pub definitions_count: usize,
    pub uses_count: usize,
    pub points: Vec<SlicePoint>,
}

pub fn analyze_slice(object_name: &str, var_name: &str, source_code: &str) -> SliceResult {
    let mut points = Vec::new();
    let var_clean = var_name.trim_start_matches('&').to_ascii_lowercase();

    let mut defs = 0;
    let mut uses = 0;

    for (idx, line_raw) in source_code.lines().enumerate() {
        let line_num = (idx + 1) as u32;
        let line = line_raw.trim();
        let line_lower = line.to_ascii_lowercase();

        if line.starts_with("//") {
            continue;
        }

        // Check if variable appears
        if line_lower.contains(&var_clean) {
            let is_def = line_lower.starts_with(&format!("&{} =", var_clean))
                || line_lower.starts_with(&format!("&{}=", var_clean))
                || line_lower.starts_with(&format!("{} =", var_clean))
                || line_lower.starts_with(&format!("{}=", var_clean));

            let kind = if is_def {
                defs += 1;
                "def"
            } else if line_lower.contains("parm(") {
                "parm"
            } else {
                uses += 1;
                "use"
            };

            points.push(SlicePoint {
                line: line_num,
                kind: kind.to_string(),
                code_snippet: line.to_string(),
            });
        }
    }

    SliceResult {
        object: object_name.to_string(),
        variable: var_name.to_string(),
        definitions_count: defs,
        uses_count: uses,
        points,
    }
}
