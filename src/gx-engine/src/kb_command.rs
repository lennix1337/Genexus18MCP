//! Argument parsing and admission for the `kb-read`, `kb-query` and `kb-write` commands.
//!
//! `parse` turns the argument list into `KbArgs`; `KbArgs::admit` validates it for one command and
//! yields the `KbOperation` to run. Neither opens a Knowledge Base, so every refusal here happens
//! before any connection, version-scope check or read.

use gx_engine::{catalog::ObjectKind, read_pagination::ReadClient};

/// Flags that take the next argument as their value (even when it starts with `--`).
const VALUE_FLAGS: [&str; 11] = [
    "--design-model",
    "--kb-version",
    "--parts",
    "--name",
    "--type",
    "--usedby",
    "--limit",
    "--offset",
    "--client",
    "--part",
    "--content-file",
];

/// Everything the command line named, before any command-specific admission.
#[derive(Debug, Default)]
pub struct KbArgs {
    positional: Vec<String>,
    parts: Option<Vec<String>>,
    name: Option<String>,
    object_type: Option<String>,
    used_by: Option<String>,
    limit: Option<usize>,
    offset: Option<usize>,
    page: bool,
    read_client: Option<String>,
    part: Option<String>,
    content_file: Option<String>,
    pub design_model: Option<i32>,
    pub kb_version: Option<i32>,
}

/// An admitted KB operation: all inputs are validated and nothing has been connected yet.
#[derive(Debug)]
pub enum KbOperation {
    Read {
        kb_path: String,
        object: String,
        parts: Option<Vec<String>>,
    },
    ReadPage {
        kb_path: String,
        object: String,
        part: String,
        offset: Option<usize>,
        limit: Option<usize>,
        client: ReadClient,
    },
    Query {
        kb_path: String,
        name: Option<String>,
        object_type: Option<String>,
        used_by: Option<String>,
        limit: usize,
    },
    Environment {
        kb_path: String,
    },
    Diagnose {
        kb_path: String,
        name: String,
    },
    VersionList {
        kb_path: String,
    },
    Inspect {
        kb_path: String,
        name: String,
        kind: ObjectKind,
    },
    ProcedureList {
        kb_path: String,
        offset: usize,
        limit: usize,
    },
    ObjectList {
        kb_path: String,
        kind: ObjectKind,
        offset: usize,
        limit: usize,
    },
    Write {
        kb_path: String,
        object: String,
        part: String,
        content: String,
        expected: Option<ObjectKind>,
    },
}

/// The integer value of `flag` at `args[i]`.
pub fn parse_id_flag(args: &[String], i: usize, flag: &str) -> Result<i32, String> {
    let value = args
        .get(i)
        .ok_or_else(|| format!("missing argument for {flag}"))?;
    parse_id(value, flag)
}

fn parse_id(value: &str, flag: &str) -> Result<i32, String> {
    value
        .parse::<i32>()
        .map_err(|_| format!("{flag} expects an integer id, got '{value}'"))
}

fn parse_count(value: &str, what: &str) -> Result<usize, String> {
    value
        .parse::<usize>()
        .map_err(|e| format!("invalid {what}: {e}"))
}

/// Parses the arguments that follow `command`.
pub fn parse(command: &str, args: &[String]) -> Result<KbArgs, String> {
    let mut parsed = KbArgs::default();
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--page" => parsed.page = true,
            // Accepted here as well so the flag works after the command name;
            // main() already consumed it when it was the first argument.
            "--diagnostics=json" => {}
            flag if VALUE_FLAGS.contains(&flag) => {
                i += 1;
                let value = args
                    .get(i)
                    .ok_or_else(|| format!("missing argument for {flag}"))?;
                parsed.set_value(flag, value)?;
            }
            other if other.starts_with("--") => {
                return Err(format!("unexpected argument for {command}: {other}"));
            }
            other => parsed.positional.push(other.to_string()),
        }
        i += 1;
    }
    Ok(parsed)
}

impl KbArgs {
    /// Stores the value of a flag listed in `VALUE_FLAGS`.
    fn set_value(&mut self, flag: &str, value: &str) -> Result<(), String> {
        match flag {
            "--design-model" => self.design_model = Some(parse_id(value, flag)?),
            "--kb-version" => self.kb_version = Some(parse_id(value, flag)?),
            "--parts" => {
                self.parts = Some(
                    value
                        .split(',')
                        .map(|part| part.trim().to_string())
                        .filter(|part| !part.is_empty())
                        .collect(),
                );
            }
            "--name" => self.name = Some(value.to_string()),
            "--type" => self.object_type = Some(value.to_string()),
            "--usedby" => self.used_by = Some(value.to_string()),
            "--limit" => self.limit = Some(parse_count(value, "limit")?),
            "--offset" => self.offset = Some(parse_count(value, "offset")?),
            "--client" => self.read_client = Some(value.to_string()),
            "--part" => self.part = Some(value.to_string()),
            "--content-file" => self.content_file = Some(value.to_string()),
            other => unreachable!("{other} is not a value flag"),
        }
        Ok(())
    }

    /// Validates the arguments for `command` and builds the operation. May read the `kb-write`
    /// content file, but never opens the Knowledge Base.
    pub fn admit(self, command: &str) -> Result<KbOperation, String> {
        match command {
            "kb-read" => self.admit_read(),
            "kb-query" => self.admit_query(),
            "kb-write" => self.admit_write(),
            "kb-environment" => Ok(KbOperation::Environment {
                kb_path: self.admit_scope_only("kb-environment")?,
            }),
            "kb-versions" => Ok(KbOperation::VersionList {
                kb_path: self.admit_scope_only("kb-versions")?,
            }),
            "kb-inspect" => self.admit_inspect(),
            "kb-diagnose" => {
                let [kb_path, name] = self.admit_scope_only_positional(
                    "kb-diagnose",
                    "kb-diagnose requires <kb-path> <object-name>",
                )?;
                Ok(KbOperation::Diagnose { kb_path, name })
            }
            "kb-list-procedures" => self.admit_procedure_list(),
            "kb-list-objects" => self.admit_object_list(),
            _ => unreachable!("kb_command is only used for native KB operations"),
        }
    }

    fn admit_inspect(mut self) -> Result<KbOperation, String> {
        let object_type = self.object_type.take();
        let kind = match object_type.as_deref() {
            Some(t) => gx_engine::kb_ops::kind_from_type_name(t)
                .ok_or_else(|| format!("unknown object type '{t}'"))?,
            None => ObjectKind::Procedure,
        };
        let [kb_path, name] = self.admit_scope_only_positional(
            "kb-inspect",
            "kb-inspect requires <kb-path> <object-name>",
        )?;
        Ok(KbOperation::Inspect {
            kb_path,
            name,
            kind,
        })
    }

    fn admit_object_list(mut self) -> Result<KbOperation, String> {
        let limit = self.limit.take().unwrap_or(200);
        let offset = self.offset.take().unwrap_or(0);
        if !(1..=200).contains(&limit) || offset > i32::MAX as usize {
            return Err(
                "kb-list-objects requires limit 1..200 and Int32 nonnegative offset".into(),
            );
        }
        let object_type = self.object_type.take();
        let kind = match object_type.as_deref() {
            Some(t) => gx_engine::kb_ops::kind_from_type_name(t)
                .ok_or_else(|| format!("unknown object type '{t}'"))?,
            None => ObjectKind::Procedure,
        };
        let kb_path = self.admit_scope_only("kb-list-objects")?;
        Ok(KbOperation::ObjectList {
            kb_path,
            kind,
            offset,
            limit,
        })
    }

    fn admit_read(self) -> Result<KbOperation, String> {
        let [kb_path, object] =
            take_positional(self.positional, "kb-read requires <kb-path> <object>")?;
        if !self.page {
            if self.offset.is_some() || self.limit.is_some() || self.read_client.is_some() {
                return Err("--offset, --limit and --client require --page for kb-read".into());
            }
            return Ok(KbOperation::Read {
                kb_path,
                object,
                parts: self.parts,
            });
        }
        let part = self
            .parts
            .and_then(|mut parts| if parts.len() == 1 { parts.pop() } else { None })
            .ok_or_else(|| "--page requires exactly one --parts <Part>".to_string())?;
        Ok(KbOperation::ReadPage {
            kb_path,
            object,
            part,
            offset: self.offset,
            limit: self.limit,
            client: page_client(self.read_client.as_deref())?,
        })
    }

    fn admit_query(self) -> Result<KbOperation, String> {
        let [kb_path] = take_positional(self.positional, "kb-query requires <kb-path>")?;
        if self.page || self.offset.is_some() || self.read_client.is_some() {
            return Err("--page, --offset and --client are only valid for kb-read".into());
        }
        Ok(KbOperation::Query {
            kb_path,
            name: self.name,
            object_type: self.object_type,
            used_by: self.used_by,
            limit: self.limit.unwrap_or(0),
        })
    }

    fn admit_procedure_list(mut self) -> Result<KbOperation, String> {
        let limit = self.limit.take().unwrap_or(200);
        let offset = self.offset.take().unwrap_or(0);
        if !(1..=200).contains(&limit) || offset > i32::MAX as usize {
            return Err(
                "kb-list-procedures requires limit 1..200 and Int32 nonnegative offset".into(),
            );
        }
        // The remaining admission is intentionally as narrow as environment reads.
        let kb_path = self.admit_scope_only("kb-list-procedures")?;
        Ok(KbOperation::ProcedureList {
            kb_path,
            offset,
            limit,
        })
    }

    /// The `<kb-path>` of a command that accepts nothing but the active-scope flags.
    fn admit_scope_only(self, command: &str) -> Result<String, String> {
        let [kb_path] =
            self.admit_scope_only_positional(command, &format!("{command} requires <kb-path>"))?;
        Ok(kb_path)
    }

    /// The `N` positional arguments of a command that accepts no flag but the active-scope ones.
    fn admit_scope_only_positional<const N: usize>(
        self,
        command: &str,
        usage: &str,
    ) -> Result<[String; N], String> {
        if self.parts.is_some()
            || self.name.is_some()
            || self.object_type.is_some()
            || self.used_by.is_some()
            || self.limit.is_some()
            || self.offset.is_some()
            || self.page
            || self.read_client.is_some()
            || self.part.is_some()
            || self.content_file.is_some()
        {
            return Err(format!(
                "{command} accepts only --design-model and --kb-version"
            ));
        }
        take_positional(self.positional, usage)
    }

    fn admit_write(self) -> Result<KbOperation, String> {
        let [kb_path, object] =
            take_positional(self.positional, "kb-write requires <kb-path> <object>")?;
        if self.page || self.offset.is_some() || self.limit.is_some() || self.read_client.is_some()
        {
            return Err("--page, --offset, --limit and --client are not valid for kb-write".into());
        }
        let part = self
            .part
            .ok_or_else(|| "kb-write requires --part <Part>".to_string())?;
        let content_file = self
            .content_file
            .ok_or_else(|| "kb-write requires --content-file <path>".to_string())?;
        let content = read_content_file(&content_file)?;
        Ok(KbOperation::Write {
            kb_path,
            object,
            part,
            content,
            expected: expected_kind(self.object_type.as_deref())?,
        })
    }
}

/// The `N` positional arguments, or `message` when the count differs.
fn take_positional<const N: usize>(
    positional: Vec<String>,
    message: &str,
) -> Result<[String; N], String> {
    positional.try_into().map_err(|_| message.to_string())
}

fn page_client(read_client: Option<&str>) -> Result<ReadClient, String> {
    match read_client.unwrap_or("mcp").to_ascii_lowercase().as_str() {
        "mcp" => Ok(ReadClient::Mcp),
        // The Worker Base64-encodes non-MCP reads and adds derived metadata;
        // this candidate only implements its MCP plain-text envelope.
        other => Err(format!(
            "unsupported --client '{other}' (the native page envelope serves only mcp)"
        )),
    }
}

fn read_content_file(path: &str) -> Result<String, String> {
    let bytes =
        std::fs::read(path).map_err(|e| format!("cannot read content file '{path}': {e}"))?;
    String::from_utf8(bytes).map_err(|_| format!("content file '{path}' is not valid UTF-8"))
}

fn expected_kind(object_type: Option<&str>) -> Result<Option<ObjectKind>, String> {
    match object_type {
        None => Ok(None),
        Some(value) if value.trim().is_empty() => Ok(None),
        Some(value) => gx_engine::kb_ops::kind_from_type_name(value)
            .map(Some)
            .ok_or_else(|| format!("unknown object type '{value}'")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    fn admitted(command: &str, values: &[&str]) -> Result<KbOperation, String> {
        parse(command, &args(values))?.admit(command)
    }

    fn refusal(command: &str, values: &[&str]) -> String {
        admitted(command, values).expect_err("must be refused")
    }

    struct TempFile(std::path::PathBuf);

    impl TempFile {
        fn new(label: &str, bytes: &[u8]) -> Self {
            let path = std::env::temp_dir()
                .join(format!("gxe-kb-command-{}-{label}.txt", std::process::id()));
            std::fs::write(&path, bytes).expect("temp content file");
            Self(path)
        }

        fn path(&self) -> &str {
            self.0.to_str().expect("utf-8 temp path")
        }
    }

    impl Drop for TempFile {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    #[test]
    fn procedure_listing_cli_admits_only_bounded_paging_and_scope() {
        assert!(matches!(
            admitted(
                "kb-list-procedures",
                &["kb", "--limit", "3", "--offset", "6"]
            )
            .unwrap(),
            KbOperation::ProcedureList {
                limit: 3,
                offset: 6,
                ..
            }
        ));
        for args in [
            vec!["kb", "--limit", "0"],
            vec!["kb", "--offset", "2147483648"],
            vec!["kb", "--name", "foo"],
            vec!["kb", "--type", "Transaction"],
        ] {
            assert!(admitted("kb-list-procedures", &args).is_err());
        }
    }

    #[test]
    fn environment_admits_only_path_and_explicit_scope_flags() {
        assert!(
            matches!(admitted("kb-environment", &["kb", "--design-model", "4", "--kb-version", "2"]).unwrap(),
            KbOperation::Environment { kb_path } if kb_path == "kb")
        );
        for flag in VALUE_FLAGS
            .into_iter()
            .filter(|flag| !matches!(*flag, "--design-model" | "--kb-version"))
        {
            assert!(
                admitted("kb-environment", &["kb", flag, "1"]).is_err(),
                "{flag}"
            );
        }
        assert!(admitted("kb-environment", &["kb", "--page"]).is_err());
        assert!(admitted("kb-environment", &["kb", "extra"]).is_err());
        assert!(admitted("kb-environment", &[]).is_err());
    }

    #[test]
    fn inspect_admits_path_name_and_explicit_scope_flags_only() {
        assert!(
            matches!(admitted("kb-inspect", &["kb", "Proc", "--design-model", "1", "--kb-version", "0"]).unwrap(),
            KbOperation::Inspect { kb_path, name, kind } if kb_path == "kb" && name == "Proc" && kind == ObjectKind::Procedure)
        );
        assert!(
            matches!(admitted("kb-inspect", &["kb", "TrnCustomer", "--type", "Transaction"]).unwrap(),
            KbOperation::Inspect { kb_path, name, kind } if kb_path == "kb" && name == "TrnCustomer" && kind == ObjectKind::Transaction)
        );
        for flag in VALUE_FLAGS
            .into_iter()
            .filter(|flag| !matches!(*flag, "--design-model" | "--kb-version" | "--type"))
        {
            assert!(
                admitted("kb-inspect", &["kb", "Proc", flag, "1"]).is_err(),
                "{flag}"
            );
        }
        assert!(admitted("kb-inspect", &["kb"]).is_err());
        assert!(admitted("kb-inspect", &["kb", "Proc", "extra"]).is_err());
    }

    #[test]
    fn diagnose_admits_path_name_and_explicit_scope_flags_only() {
        assert!(
            matches!(admitted("kb-diagnose", &["kb", "Proc", "--design-model", "1", "--kb-version", "0"]).unwrap(),
            KbOperation::Diagnose { kb_path, name } if kb_path == "kb" && name == "Proc")
        );
        for flag in VALUE_FLAGS
            .into_iter()
            .filter(|flag| !matches!(*flag, "--design-model" | "--kb-version"))
        {
            assert!(
                admitted("kb-diagnose", &["kb", "Proc", flag, "1"]).is_err(),
                "{flag}"
            );
        }
        assert!(admitted("kb-diagnose", &["kb", "Proc", "--page"]).is_err());
        assert!(admitted("kb-diagnose", &["kb"]).is_err());
        assert!(admitted("kb-diagnose", &["kb", "Proc", "extra"]).is_err());
    }

    #[test]
    fn list_objects_admits_types_and_pagination() {
        assert!(
            matches!(admitted("kb-list-objects", &["kb", "--type", "Transaction", "--offset", "10", "--limit", "5"]).unwrap(),
            KbOperation::ObjectList { kb_path, kind, offset, limit } if kb_path == "kb" && kind == ObjectKind::Transaction && offset == 10 && limit == 5)
        );
    }

    #[test]
    fn version_list_admits_only_path_and_explicit_scope_flags() {
        assert!(
            matches!(admitted("kb-versions", &["kb", "--design-model", "1", "--kb-version", "0"]).unwrap(),
            KbOperation::VersionList { kb_path } if kb_path == "kb")
        );
        for flag in VALUE_FLAGS
            .into_iter()
            .filter(|flag| !matches!(*flag, "--design-model" | "--kb-version"))
        {
            assert!(
                admitted("kb-versions", &["kb", flag, "1"]).is_err(),
                "{flag}"
            );
        }
        assert!(admitted("kb-versions", &["kb", "--page"]).is_err());
        assert!(admitted("kb-versions", &["kb", "extra"]).is_err());
        assert!(admitted("kb-versions", &[]).is_err());
    }

    #[test]
    fn read_keeps_tolerating_legacy_cross_command_filters() {
        let operation = admitted(
            "kb-read",
            &[
                "kb",
                "Procedure:P",
                "--name",
                "ignored",
                "--type",
                "Procedure",
                "--usedby",
                "Other",
                "--part",
                "Rules",
            ],
        )
        .unwrap();
        assert!(
            matches!(operation, KbOperation::Read { object, parts: None, .. } if object == "Procedure:P")
        );
    }

    #[test]
    fn every_value_flag_is_handled_and_consumes_the_next_argument() {
        for flag in VALUE_FLAGS {
            let value = "1";
            let parsed = parse("kb-read", &args(&[flag, value])).expect(flag);
            assert!(
                parsed.positional.is_empty(),
                "{flag} left its value positional"
            );
            assert_eq!(
                parse("kb-read", &args(&[flag])).expect_err(flag),
                format!("missing argument for {flag}")
            );
        }
        // A value flag takes the next argument even when it looks like a flag.
        let parsed = parse("kb-query", &args(&["--name", "--type"])).unwrap();
        assert_eq!(parsed.name.as_deref(), Some("--type"));
    }

    #[test]
    fn parse_rejects_bad_numbers_and_unknown_flags_with_the_established_text() {
        assert_eq!(
            parse("kb-read", &args(&["--design-model", "x"])).unwrap_err(),
            "--design-model expects an integer id, got 'x'"
        );
        assert_eq!(
            parse("kb-read", &args(&["--kb-version", "1.5"])).unwrap_err(),
            "--kb-version expects an integer id, got '1.5'"
        );
        assert!(
            parse("kb-read", &args(&["--limit", "-1"]))
                .unwrap_err()
                .starts_with("invalid limit: ")
        );
        assert!(
            parse("kb-read", &args(&["--offset", "x"]))
                .unwrap_err()
                .starts_with("invalid offset: ")
        );
        assert_eq!(
            parse("kb-query", &args(&["--bogus"])).unwrap_err(),
            "unexpected argument for kb-query: --bogus"
        );
    }

    #[test]
    fn parse_collects_every_flag_and_ignores_diagnostics_json() {
        let parsed = parse(
            "kb-read",
            &args(&[
                "kb",
                "--diagnostics=json",
                "Obj",
                "--parts",
                " Source, ,Rules ",
                "--name",
                "n",
                "--type",
                "t",
                "--usedby",
                "u",
                "--limit",
                "5",
                "--offset",
                "2",
                "--page",
                "--client",
                "mcp",
                "--part",
                "p",
                "--content-file",
                "f",
                "--design-model",
                "3",
                "--kb-version",
                "7",
            ]),
        )
        .unwrap();
        assert_eq!(parsed.positional, ["kb", "Obj"]);
        assert_eq!(parsed.parts, Some(args(&["Source", "Rules"])));
        assert_eq!(parsed.limit, Some(5));
        assert_eq!(parsed.offset, Some(2));
        assert!(parsed.page);
        assert_eq!((parsed.design_model, parsed.kb_version), (Some(3), Some(7)));
        assert_eq!(parsed.content_file.as_deref(), Some("f"));
    }

    #[test]
    fn later_occurrence_of_a_flag_wins() {
        let parsed = parse("kb-query", &args(&["--limit", "1", "--limit", "2"])).unwrap();
        assert_eq!(parsed.limit, Some(2));
    }

    #[test]
    fn kb_read_admits_plain_and_page_forms() {
        match admitted("kb-read", &["kb", "Obj", "--parts", "Source,Rules"]).unwrap() {
            KbOperation::Read {
                kb_path,
                object,
                parts,
            } => {
                assert_eq!((kb_path.as_str(), object.as_str()), ("kb", "Obj"));
                assert_eq!(parts, Some(args(&["Source", "Rules"])));
            }
            other => panic!("{other:?}"),
        }
        match admitted(
            "kb-read",
            &[
                "kb", "Obj", "--page", "--parts", "Source", "--offset", "4", "--limit", "9",
            ],
        )
        .unwrap()
        {
            KbOperation::ReadPage {
                part,
                offset,
                limit,
                client,
                ..
            } => {
                assert_eq!(part, "Source");
                assert_eq!((offset, limit), (Some(4), Some(9)));
                assert_eq!(client, ReadClient::Mcp);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(
            admitted(
                "kb-read",
                &[
                    "kb", "Obj", "--page", "--parts", "Source", "--client", "MCP"
                ]
            ),
            Ok(KbOperation::ReadPage {
                client: ReadClient::Mcp,
                ..
            })
        ));
    }

    #[test]
    fn kb_read_refusals_keep_their_order_and_text() {
        assert_eq!(
            refusal("kb-read", &["kb"]),
            "kb-read requires <kb-path> <object>"
        );
        assert_eq!(
            refusal("kb-read", &["kb", "a", "b"]),
            "kb-read requires <kb-path> <object>"
        );
        for flags in [
            &["--offset", "1"][..],
            &["--limit", "1"],
            &["--client", "mcp"],
        ] {
            let mut values = vec!["kb", "Obj"];
            values.extend_from_slice(flags);
            assert_eq!(
                refusal("kb-read", &values),
                "--offset, --limit and --client require --page for kb-read"
            );
        }
        let no_parts: &[&str] = &[];
        for parts in [no_parts, &["--parts", ""], &["--parts", "A,B"]] {
            let mut values = vec!["kb", "Obj", "--page"];
            values.extend_from_slice(parts);
            assert_eq!(
                refusal("kb-read", &values),
                "--page requires exactly one --parts <Part>"
            );
        }
    }

    #[test]
    fn page_refuses_other_clients_before_any_connection() {
        for client in ["ide", "IDE", "other", ""] {
            let message = refusal(
                "kb-read",
                &[
                    "kb", "Obj", "--page", "--parts", "Source", "--client", client,
                ],
            );
            assert_eq!(
                message,
                format!(
                    "unsupported --client '{}' (the native page envelope serves only mcp)",
                    client.to_ascii_lowercase()
                )
            );
        }
        // The part count is checked before the client.
        assert_eq!(
            refusal("kb-read", &["kb", "Obj", "--page", "--client", "ide"]),
            "--page requires exactly one --parts <Part>"
        );
    }

    #[test]
    fn kb_query_admits_filters_and_refuses_page_flags() {
        match admitted(
            "kb-query",
            &["kb", "--name", "N", "--type", "Procedure", "--usedby", "U"],
        )
        .unwrap()
        {
            KbOperation::Query {
                name,
                object_type,
                used_by,
                limit,
                ..
            } => {
                assert_eq!(name.as_deref(), Some("N"));
                assert_eq!(object_type.as_deref(), Some("Procedure"));
                assert_eq!(used_by.as_deref(), Some("U"));
                assert_eq!(limit, 0);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(
            admitted("kb-query", &["kb", "--limit", "7"]),
            Ok(KbOperation::Query { limit: 7, .. })
        ));
        assert_eq!(refusal("kb-query", &[]), "kb-query requires <kb-path>");
        for flags in [&["--page"][..], &["--offset", "1"], &["--client", "mcp"]] {
            let mut values = vec!["kb"];
            values.extend_from_slice(flags);
            assert_eq!(
                refusal("kb-query", &values),
                "--page, --offset and --client are only valid for kb-read"
            );
        }
    }

    #[test]
    fn kb_write_admits_content_and_expected_type() {
        let content = TempFile::new("admit", "été".as_bytes());
        match admitted(
            "kb-write",
            &[
                "kb",
                "Obj",
                "--part",
                "Source",
                "--content-file",
                content.path(),
                "--type",
                "proc",
            ],
        )
        .unwrap()
        {
            KbOperation::Write {
                part,
                content,
                expected,
                ..
            } => {
                assert_eq!((part.as_str(), content.as_str()), ("Source", "été"));
                assert_eq!(expected, Some(ObjectKind::Procedure));
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(
            admitted(
                "kb-write",
                &[
                    "kb",
                    "Obj",
                    "--part",
                    "S",
                    "--content-file",
                    content.path(),
                    "--type",
                    " "
                ]
            ),
            Ok(KbOperation::Write { expected: None, .. })
        ));
    }

    #[test]
    fn kb_write_refusals_keep_their_order_and_text() {
        let valid = TempFile::new("refuse-valid", b"x");
        let invalid = TempFile::new("refuse-invalid", &[0xff, 0xfe]);
        assert_eq!(
            refusal("kb-write", &["kb"]),
            "kb-write requires <kb-path> <object>"
        );
        for flags in [
            &["--page"][..],
            &["--offset", "1"],
            &["--limit", "1"],
            &["--client", "mcp"],
        ] {
            let mut values = vec!["kb", "Obj"];
            values.extend_from_slice(flags);
            assert_eq!(
                refusal("kb-write", &values),
                "--page, --offset, --limit and --client are not valid for kb-write"
            );
        }
        assert_eq!(
            refusal("kb-write", &["kb", "Obj"]),
            "kb-write requires --part <Part>"
        );
        assert_eq!(
            refusal("kb-write", &["kb", "Obj", "--part", "S"]),
            "kb-write requires --content-file <path>"
        );
        assert!(
            refusal(
                "kb-write",
                &[
                    "kb",
                    "Obj",
                    "--part",
                    "S",
                    "--content-file",
                    "C:/__gxe_missing__/x"
                ]
            )
            .starts_with("cannot read content file 'C:/__gxe_missing__/x': ")
        );
        assert_eq!(
            refusal(
                "kb-write",
                &["kb", "Obj", "--part", "S", "--content-file", invalid.path()]
            ),
            format!("content file '{}' is not valid UTF-8", invalid.path())
        );
        assert_eq!(
            refusal(
                "kb-write",
                &[
                    "kb",
                    "Obj",
                    "--part",
                    "S",
                    "--content-file",
                    valid.path(),
                    "--type",
                    "Nope"
                ]
            ),
            "unknown object type 'Nope'"
        );
        // An unreadable content file is reported before an unknown type.
        assert!(
            refusal(
                "kb-write",
                &[
                    "kb",
                    "Obj",
                    "--part",
                    "S",
                    "--content-file",
                    "C:/__gxe_missing__/x",
                    "--type",
                    "Nope"
                ]
            )
            .starts_with("cannot read content file")
        );
    }
}
