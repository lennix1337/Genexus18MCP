mod analysis;
mod cache;
mod graph;
mod model;
mod output;
mod parser;

use clap::Parser;
use std::path::PathBuf;

#[derive(Parser, Debug)]
#[command(
    name = "gxwire",
    version = "0.1.0",
    about = "High-performance architectural discovery, call graph, and impact engine for GeneXus",
    long_about = "GxWire is an ultrafast, token-budgeted static analysis and graph engine for GeneXus Knowledge Bases."
)]
struct Args {
    /// Show live Knowledge Base status & architectural hubs (content-first default)
    #[arg(long)]
    status: bool,

    /// Query 1-hop and transitive callers of a symbol
    #[arg(long)]
    callers: Option<String>,

    /// Compute blast radius and change impact for an object or attribute
    #[arg(long)]
    impact: Option<String>,

    /// Intent-based ranking of GeneXus symbols for a task
    #[arg(long = "for")]
    for_task: Option<String>,

    /// Trace data-flow definition and uses of a variable inside an object (e.g. ProcName:VarName)
    #[arg(long)]
    slice: Option<String>,

    /// Complete orientation bundle under a shared token budget
    #[arg(long)]
    pack_task: Option<String>,

    /// Trace call or dependency path from symbol to symbol (e.g. --from A --to B or --path A:B)
    #[arg(long)]
    path: Option<String>,

    /// Source symbol for path tracing
    #[arg(long)]
    from: Option<String>,

    /// Target symbol for path tracing
    #[arg(long)]
    to: Option<String>,

    /// Evaluate if a symbol can be safely deleted without breaking callers or schemas
    #[arg(long)]
    safe_delete: Option<String>,

    /// Re-index source directory or KB cache
    #[arg(long)]
    index: Option<Option<PathBuf>>,

    /// Self-diagnostic and cache status
    #[arg(long)]
    doctor: bool,

    /// Output format: xml, json, toon, mermaid
    #[arg(long, default_value = "xml")]
    format: String,

    /// Token budget cap for output bundles
    #[arg(long, default_value_t = 1200)]
    token_budget: usize,

    /// Bypass token budget clipping and output full untruncated graph/results
    #[arg(long)]
    full: bool,

    /// Target directory to inspect
    #[arg(long, default_value = ".")]
    dir: PathBuf,
}

fn emit_error(format: &str, code: &str, target: &str, message: &str, next: &str) {
    if format == "json" {
        let err_obj = serde_json::json!({
            "error": {
                "code": code,
                "target": target,
                "message": message
            },
            "next": next
        });
        eprintln!("{}", serde_json::to_string_pretty(&err_obj).unwrap_or_default());
    } else if format == "toon" {
        eprint!("{}", output::format_error_toon(code, target, message, next));
    } else {
        eprintln!("{}", output::format_error_xml(code, target, message, next));
    }
    std::process::exit(1);
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args = Args::parse();
    let root = args.dir.canonicalize().unwrap_or(args.dir.clone());

    if args.doctor {
        let cache_path = cache::get_cache_path(&root);
        let exists = cache_path.exists();
        let doc_res = model::DoctorResult {
            schema: "gxwire.doctor/v1".to_string(),
            version: "0.1.0".to_string(),
            root: root.display().to_string(),
            cache_exists: exists,
            cache_path: cache_path.display().to_string(),
            status: "ready".to_string(),
            next: if exists {
                "gxwire --for \"<task>\" or gxwire --pack-task \"<task>\"".to_string()
            } else {
                format!("gxwire --index \"{}\" to build fast cache", root.display())
            },
        };
        if args.format == "json" {
            println!("{}", serde_json::to_string_pretty(&doc_res)?);
        } else if args.format == "toon" {
            print!("{}", output::format_doctor_toon(&doc_res));
        } else {
            println!("{}", output::format_doctor_xml(&doc_res));
        }
        return Ok(());
    }

    if let Some(index_dir_opt) = args.index {
        let target_dir = index_dir_opt.unwrap_or(root.clone());
        eprintln!("[gxwire] Building index from: {}", target_dir.display());
        let g = cache::build_graph_from_directory(&target_dir)?;
        cache::save_cache(&g, &root)?;
        eprintln!(
            "[gxwire] Index built successfully! Nodes: {}, Cache: {}",
            g.nodes.len(),
            cache::get_cache_path(&root).display()
        );
        return Ok(());
    }

    // Try loading existing cache or build on the fly
    let g = match cache::load_cache(&root) {
        Ok(cached) => cached,
        Err(_) => {
            // Build temporary graph from directory if no cache
            cache::build_graph_from_directory(&root)?
        }
    };

    if let Some(target) = args.callers {
        if let Some(res) = analysis::analyze_callers_ext(&g, &target, 4, args.full) {
            if args.format == "mermaid" {
                print!("{}", output::format_callers_mermaid(&res));
            } else if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else if args.format == "toon" {
                print!("{}", output::format_callers_toon(&res));
            } else {
                println!("{}", output::format_callers_xml(&res));
            }
        } else {
            emit_error(
                &args.format,
                "SymbolNotFound",
                &target,
                "Symbol not found in graph",
                &format!("gxwire --for \"{}\" to find similar symbols", target),
            );
        }
        return Ok(());
    }

    if let Some(target) = args.impact {
        if let Some(res) = analysis::analyze_impact_ext(&g, &target, args.full) {
            if args.format == "mermaid" {
                print!("{}", output::format_impact_mermaid(&res));
            } else if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else if args.format == "toon" {
                print!("{}", output::format_impact_toon(&res));
            } else {
                println!("{}", output::format_impact_xml(&res));
            }
        } else {
            emit_error(
                &args.format,
                "SymbolNotFound",
                &target,
                "Symbol not found in graph",
                &format!("gxwire --for \"{}\" to find similar symbols", target),
            );
        }
        return Ok(());
    }

    let path_endpoints = if let (Some(f), Some(t)) = (args.from.as_ref(), args.to.as_ref()) {
        Some((f.clone(), t.clone()))
    } else if let Some(ref p_str) = args.path {
        let parts: Vec<&str> = if p_str.contains("->") {
            p_str.split("->").collect()
        } else {
            p_str.split(':').collect()
        };
        if parts.len() >= 2 {
            Some((parts[0].trim().to_string(), parts[1].trim().to_string()))
        } else {
            None
        }
    } else {
        None
    };

    if let Some((from_sym, to_sym)) = path_endpoints {
        if let Some(res) = analysis::analyze_path_ext(&g, &from_sym, &to_sym, args.full) {
            if args.format == "mermaid" {
                print!("{}", output::format_path_mermaid(&res));
            } else if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else if args.format == "toon" {
                print!("{}", output::format_path_toon(&res));
            } else {
                println!("{}", output::format_path_xml(&res));
            }
        } else {
            emit_error(
                &args.format,
                "SymbolNotFound",
                &format!("{} -> {}", from_sym, to_sym),
                "One or both symbols not found in graph",
                &format!("gxwire --for \"{}\" or gxwire --for \"{}\" to find exact symbol names", from_sym, to_sym),
            );
        }
        return Ok(());
    }

    if let Some(task) = args.for_task {
        let res = analysis::search_for_task_ext(&g, &task, 15, args.full);
        if args.format == "json" {
            println!("{}", serde_json::to_string_pretty(&res)?);
        } else if args.format == "toon" {
            print!("{}", output::format_search_toon(&res));
        } else {
            println!("{}", output::format_search_xml(&res));
        }
        return Ok(());
    }

    if let Some(task) = args.pack_task {
        let budget = if args.full { usize::MAX } else { args.token_budget };
        let res = analysis::pack_task_ext(&g, &task, budget, args.full);
        if args.format == "mermaid" {
            print!("{}", output::format_pack_task_mermaid(&res));
        } else if args.format == "json" {
            println!("{}", serde_json::to_string_pretty(&res)?);
        } else if args.format == "toon" {
            print!("{}", output::format_pack_task_toon(&res));
        } else {
            println!("{}", output::format_pack_task_xml(&res));
        }
        return Ok(());
    }

    if let Some(slice_target) = args.slice {
        let parts: Vec<&str> = slice_target.split(':').collect();
        let obj_name = parts[0];
        let var_name = if parts.len() > 1 { parts[1] } else { "" };

        if let Some(node) = g.get_node_by_name(obj_name) {
            let content = if let Some(ref p) = node.path {
                std::fs::read_to_string(p).unwrap_or_default()
            } else {
                String::new()
            };
            let res = analysis::analyze_slice(obj_name, var_name, &content);
            if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else if args.format == "toon" {
                print!("{}", output::format_slice_toon(&res));
            } else {
                println!("{}", output::format_slice_xml(&res));
            }
        } else {
            emit_error(
                &args.format,
                "SymbolNotFound",
                obj_name,
                "Object not found in graph",
                &format!("gxwire --for \"{}\" to find similar symbols", obj_name),
            );
        }
        return Ok(());
    }

    if let Some(target) = args.safe_delete {
        if let Some(res) = analysis::analyze_impact_ext(&g, &target, args.full) {
            let can_delete = res.blast_radius == 0 && res.callers.is_empty() && res.writers.is_empty();
            let safe_del = model::SafeDeleteResult {
                schema: "gxwire.safe_delete/v1".to_string(),
                symbol: target.clone(),
                can_delete,
                blast_radius: res.blast_radius,
                callers_count: res.callers.len(),
                empty: res.blast_radius == 0,
                next: if can_delete {
                    format!("genexus_edit {} or genexus_delete {}", target, target)
                } else {
                    format!("gxwire --impact {} to inspect blockers", target)
                },
            };
            if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&safe_del)?);
            } else if args.format == "toon" {
                print!("{}", output::format_safe_delete_toon(&safe_del));
            } else {
                println!("{}", output::format_safe_delete_xml(&safe_del));
            }
        } else {
            emit_error(
                &args.format,
                "SymbolNotFound",
                &target,
                "Symbol not found in graph",
                &format!("gxwire --for \"{}\" to find similar symbols", target),
            );
        }
        return Ok(());
    }

    // Principle #8: Content First (0 args defaults to live KB graph status)
    let cache_path = cache::get_cache_path(&root);
    let status_res = analysis::compute_status(&g, &root, &cache_path);
    if args.format == "mermaid" {
        print!("{}", output::format_status_mermaid(&status_res));
    } else if args.format == "json" {
        println!("{}", serde_json::to_string_pretty(&status_res)?);
    } else if args.format == "toon" {
        print!("{}", output::format_status_toon(&status_res));
    } else {
        println!("{}", output::format_status_xml(&status_res));
    }

    Ok(())
}
