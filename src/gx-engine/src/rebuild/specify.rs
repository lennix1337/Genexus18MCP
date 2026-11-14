//! Phase 1: specifies every Procedure's navigation and verifies every Transaction's formulas.

use super::{
    RebuildOptions,
    classify::ObjectManifest,
    parallel::{Findings, map_chunks},
    reuse::{Session, SpecRecord, digest_of, key_of},
};
use crate::{
    catalog::{Catalog, KbObject, ObjectKind},
    csharp::webform,
    navigation::{
        ExtendedTable, NavigationReport, compute_extended_tables, specify_procedure_with_tables,
        specify_transaction_attribute_formula,
    },
    table::{SqlDialect, Table, derive_tables_from_transaction, derive_tables_partial},
    transaction::{TransactionLevel, TransactionStructure},
};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap},
    sync::atomic::Ordering,
    time::Instant,
};

/// The artifact cache kind of a Procedure's navigation report.
const SPEC_KIND: &str = "spec";

/// What [`specify_phase`] returns: the navigation reports, the elapsed milliseconds, and each successful
/// specification by object id.
type SpecifyOutcome = (Vec<NavigationReport>, u64, HashMap<String, SpecRecord>);

/// The navigation reports, the elapsed milliseconds, and each successful specification by object id under
/// the fingerprint of what it read, for the next run to reuse.
pub(super) fn specify_phase(
    manifest: &ObjectManifest<'_>,
    catalog: &Catalog,
    concurrency: usize,
    options: &RebuildOptions,
    findings: &mut Findings,
    session: &Session<'_>,
) -> Result<SpecifyOutcome, String> {
    let spec_start = Instant::now();

    // Each Transaction derives or is refused on its own. A refused one is an error naming it, and the tables
    // of the others are still derived, so the Procedures that do not use it are specified.
    let (tables, physical_failures) = derive_tables_partial(catalog);
    if options.fail_fast {
        if let Some(err) = physical_failures.first() {
            return Err(format!("failed to derive physical tables: {err}"));
        }
    }
    findings.errors.extend(
        physical_failures
            .iter()
            .map(|err| format!("physical schema error: {err}")),
    );
    let extended_tables = compute_extended_tables(&tables);
    let dialect = options.sql_dialect;

    // A Procedure's specification reads the Procedure, the tables and the dialect, and nothing else.
    let inputs = key_of(
        "specification inputs",
        [
            session.engine_key(),
            digest_of(&tables).as_str(),
            digest_of(&dialect).as_str(),
        ],
    );

    let mut reports = Vec::new();
    let mut records = HashMap::new();
    for specified in map_chunks(&manifest.procedures, concurrency, |chunk| {
        specify_procedures(chunk, &tables, &extended_tables, dialect, session, &inputs)
    })
    .into_iter()
    .flatten()
    {
        match specified.outcome {
            Ok(report) => {
                findings.warnings.extend(report.warnings.iter().cloned());
                records.insert(
                    specified.id,
                    SpecRecord {
                        key: specified.key,
                        report: report.clone(),
                    },
                );
                reports.push(report);
            }
            Err(err) => findings.errors.push(format!(
                "Procedure '{}' specification error: {err}",
                specified.name
            )),
        }
    }

    // A Transaction whose web class is emitted checks its referenced keys there.
    let web_checked: BTreeSet<String> = manifest
        .webpanels
        .iter()
        .filter_map(|class| match class {
            webform::WebClass::Transaction(form) => Some(form.name.clone()),
            webform::WebClass::WorkWith(class) => class.transaction_name().map(str::to_string),
            _ => None,
        })
        .collect();
    for chunk_findings in map_chunks(&manifest.transactions, concurrency, |chunk| {
        verify_transactions(chunk, catalog, &tables, &web_checked, dialect)
    }) {
        findings.merge(chunk_findings);
    }

    Ok((reports, spec_start.elapsed().as_millis() as u64, records))
}

/// One Procedure's specification, as computed or as reused, under its fingerprint.
struct Specified {
    id: String,
    name: String,
    key: String,
    outcome: Result<NavigationReport, String>,
}

/// Specifies each Procedure, in order. A report the previous run recorded, or the artifact cache holds,
/// under the same fingerprint is that Procedure's report; an error is never reused.
fn specify_procedures(
    procedures: &[&KbObject],
    tables: &[Table],
    extended_tables: &BTreeMap<String, ExtendedTable>,
    dialect: SqlDialect,
    session: &Session<'_>,
    inputs: &str,
) -> Vec<Specified> {
    let mut specified = Vec::new();
    for proc in procedures {
        // Admitted DataProviders have no database navigation: their typed AST was
        // validated during classification and uses the checked Procedure emitter.
        if proc.kind == ObjectKind::DataProvider {
            continue;
        }
        let key = key_of("specification", [session.content(proc).as_str(), inputs]);
        let recorded = session
            .previous_procedure(proc.id.as_str())
            .and_then(|record| record.spec.as_ref())
            .filter(|spec| spec.key == key);
        let outcome = if let Some(spec) = recorded {
            session.specifications_kept.fetch_add(1, Ordering::Relaxed);
            Ok(spec.report.clone())
        } else {
            session
                .specifications_computed
                .fetch_add(1, Ordering::Relaxed);
            match session.cache().and_then(|cache| cache.get(SPEC_KIND, &key)) {
                Some(report) => Ok(report),
                None => {
                    let outcome =
                        specify_procedure_with_tables(proc, tables, extended_tables, dialect);
                    if let (Some(cache), Ok(report)) = (session.cache(), &outcome) {
                        cache.put(SPEC_KIND, &key, report);
                    }
                    outcome
                }
            }
        };
        specified.push(Specified {
            id: proc.id.as_str().to_string(),
            name: proc.name.clone(),
            key,
            outcome,
        });
    }
    specified
}

/// Verifies the formulas of each Transaction and warns about what its STRUCTURE part leaves unchecked.
fn verify_transactions(
    transactions: &[&KbObject],
    catalog: &Catalog,
    all_tables: &[Table],
    web_checked: &BTreeSet<String>,
    dialect: SqlDialect,
) -> Findings {
    let mut findings = Findings::default();
    for txn in transactions {
        let Some(part) = txn.source_parts.iter().find(|p| p.name == "STRUCTURE") else {
            continue;
        };
        let Ok(structure) = serde_json::from_str::<TransactionStructure>(&part.source) else {
            // Not a silent skip: the STRUCTURE part is not the JSON form
            // this reader parses, so formula verification did not run.
            findings.warnings.push(format!(
                "Transaction '{}': STRUCTURE part is not the JSON form this build reads; its formula verification was skipped",
                txn.name
            ));
            continue;
        };
        if !structure.key_references.is_empty() {
            findings
                .warnings
                .push(key_references_warning(&txn.name, web_checked));
        }
        let resolve_attr = |name: &str| -> Option<crate::model::DataType> {
            let mut visited = BTreeSet::new();
            crate::kb::resolve_domain_or_attribute_data_type(catalog, name, &mut visited, 0).ok()
        };
        let txn_tables =
            derive_tables_from_transaction(&structure, resolve_attr).unwrap_or_default();
        verify_level_formulas(
            &structure.root,
            &txn_tables,
            all_tables,
            dialect,
            &mut findings,
        );
    }
    findings
}

fn key_references_warning(transaction: &str, web_checked: &BTreeSet<String>) -> String {
    if web_checked.contains(transaction) {
        format!(
            "Transaction '{transaction}': its web class checks its referenced keys, but foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all"
        )
    } else {
        format!(
            "Transaction '{transaction}': key-existence checks are not generated; foreign keys are metadata only, and no DDL is emitted or applied by rebuild-all"
        )
    }
}

fn verify_level_formulas(
    level: &TransactionLevel,
    txn_tables: &[Table],
    all_tables: &[Table],
    dialect: SqlDialect,
    findings: &mut Findings,
) {
    for attr in &level.attributes {
        if let Some(formula_src) = &attr.formula {
            if let Err(e) = specify_transaction_attribute_formula(
                &attr.name,
                formula_src,
                txn_tables,
                all_tables,
                dialect,
            ) {
                findings
                    .errors
                    .push(format!("Transaction formula error on '{}': {e}", attr.name));
            }
        }
    }
    for sub in &level.levels {
        verify_level_formulas(sub, txn_tables, all_tables, dialect, findings);
    }
}
