//! `gx-engine kb-analyze <kb-path> [--design-model <id> --kb-version <id>]`: reads the whole catalog of the
//! selected Design model once and prints the `analyze::Analysis` as one line of JSON on stdout. Strictly
//! read-only. Version scoping follows the other `kb-*` reads (a refusal exits with code 4).

use crate::kb_command::parse_id_flag;
use gx_engine::diagnostics::DiagnosticCategory;
use std::sync::Arc;

pub fn run(args: &[String], category: &mut DiagnosticCategory) -> Result<(), String> {
    *category = DiagnosticCategory::Input;
    let mut kb_path = None;
    let mut design_model = None;
    let mut kb_version = None;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--diagnostics=json" => {}
            "--design-model" => {
                i += 1;
                design_model = Some(parse_id_flag(args, i, "--design-model")?);
            }
            "--kb-version" => {
                i += 1;
                kb_version = Some(parse_id_flag(args, i, "--kb-version")?);
            }
            other if other.starts_with("--") => {
                return Err(format!("unexpected argument for kb-analyze: {other}"));
            }
            other => {
                if kb_path.replace(other.to_string()).is_some() {
                    return Err("kb-analyze takes one Knowledge Base path".into());
                }
            }
        }
        i += 1;
    }
    let kb_path = kb_path.ok_or("kb-analyze requires a Knowledge Base path")?;
    let active = crate::active_version(design_model, kb_version)?;
    let client = crate::connect_selected_kb(&kb_path, active, category)?;
    let catalog = gx_engine::udm::UdmReader::new(Arc::new(client)).read_catalog()?;
    crate::print_json(&gx_engine::analyze::analyze(&catalog))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_with(args: &[&str]) -> (Result<(), String>, DiagnosticCategory) {
        let args: Vec<String> = args.iter().map(|arg| arg.to_string()).collect();
        let mut category = DiagnosticCategory::Usage;
        (run(&args, &mut category), category)
    }

    #[test]
    fn a_missing_path_an_unknown_flag_and_two_paths_are_refused_before_any_read() {
        assert_eq!(
            run_with(&[]).0.unwrap_err(),
            "kb-analyze requires a Knowledge Base path"
        );
        assert!(
            run_with(&["kb", "--bogus"])
                .0
                .unwrap_err()
                .contains("--bogus")
        );
        assert_eq!(
            run_with(&["a", "b"]).0.unwrap_err(),
            "kb-analyze takes one Knowledge Base path"
        );
    }

    #[test]
    fn unpaired_version_flags_are_refused_like_the_other_kb_commands() {
        let (result, category) = run_with(&["kb", "--design-model", "3"]);
        assert!(
            result
                .unwrap_err()
                .starts_with("--design-model and --kb-version name")
        );
        assert_eq!(category, DiagnosticCategory::Input);
    }

    #[test]
    fn a_directory_that_does_not_exist_is_an_error_not_an_empty_analysis() {
        let (result, _) = run_with(&["C:\\no\\such\\kb\\directory"]);
        assert!(result.unwrap_err().contains("does not exist"));
    }
}
