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

    /// Evaluate if a symbol can be safely deleted without breaking callers or schemas
    #[arg(long)]
    safe_delete: Option<String>,

    /// Re-index source directory or KB cache
    #[arg(long)]
    index: Option<Option<PathBuf>>,

    /// Self-diagnostic and cache status
    #[arg(long)]
    doctor: bool,

    /// Output format: xml, json
    #[arg(long, default_value = "xml")]
    format: String,

    /// Token budget cap for output bundles
    #[arg(long, default_value_t = 1200)]
    token_budget: usize,

    /// Target directory to inspect
    #[arg(long, default_value = ".")]
    dir: PathBuf,
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args = Args::parse();
    let root = args.dir.canonicalize().unwrap_or(args.dir.clone());

    if args.doctor {
        let cache_path = cache::get_cache_path(&root);
        let exists = cache_path.exists();
        println!(
            "<doctor version=\"0.1.0\" root=\"{}\" cache_exists=\"{}\" cache_path=\"{}\" status=\"ready\" />",
            root.display(),
            exists,
            cache_path.display()
        );
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
        if let Some(res) = analysis::analyze_callers(&g, &target, 4) {
            if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else {
                println!("{}", output::format_callers_xml(&res));
            }
        } else {
            eprintln!("<error code=\"SymbolNotFound\" target=\"{}\" />", target);
            std::process::exit(1);
        }
        return Ok(());
    }

    if let Some(target) = args.impact {
        if let Some(res) = analysis::analyze_impact(&g, &target) {
            if args.format == "json" {
                println!("{}", serde_json::to_string_pretty(&res)?);
            } else {
                println!("{}", output::format_impact_xml(&res));
            }
        } else {
            eprintln!("<error code=\"SymbolNotFound\" target=\"{}\" />", target);
            std::process::exit(1);
        }
        return Ok(());
    }

    if let Some(task) = args.for_task {
        let res = analysis::search_for_task(&g, &task, 15);
        if args.format == "json" {
            println!("{}", serde_json::to_string_pretty(&res)?);
        } else {
            println!("{}", output::format_search_xml(&res));
        }
        return Ok(());
    }

    if let Some(task) = args.pack_task {
        let res = analysis::pack_task(&g, &task, args.token_budget);
        if args.format == "json" {
            println!("{}", serde_json::to_string_pretty(&res)?);
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
            } else {
                println!("{}", output::format_slice_xml(&res));
            }
        } else {
            eprintln!("<error code=\"SymbolNotFound\" target=\"{}\" />", obj_name);
            std::process::exit(1);
        }
        return Ok(());
    }

    if let Some(target) = args.safe_delete {
        if let Some(res) = analysis::analyze_impact(&g, &target) {
            let can_delete = res.blast_radius == 0;
            println!(
                r#"<safe-delete symbol="{}" can_delete="{}" blast_radius="{}" callers="{}" />"#,
                target,
                can_delete,
                res.blast_radius,
                res.callers.len()
            );
        } else {
            eprintln!("<error code=\"SymbolNotFound\" target=\"{}\" />", target);
            std::process::exit(1);
        }
        return Ok(());
    }

    eprintln!("[gxwire] No command provided. Run `gxwire --help` for available options.");
    Ok(())
}
