# Documentation index

Every Markdown document directly under `docs/` is listed here, grouped by what
you are trying to do. `cli/docs.test.js` fails when a new document is added
without an entry, or when an entry points to a file that no longer exists.

Start with the [project README](../README.md). Contributors and AI agents should
also read [`AGENTS.md`](../AGENTS.md).

## Getting started and operating the server

- [Guia de início (português)](GETTING_STARTED.pt-br.md) and
  [Guía de inicio (español)](GETTING_STARTED.es.md) — install and first KB edit in 5–10 minutes.
- [Troubleshooting](../TROUBLESHOOTING.md) — common install, launcher and Worker failures.
- [Supported GeneXus versions](generated/supported-versions.md) — generated from `config/gx-versions.json`.
- [SDK compatibility](sdk-compatibility.md) — how the Worker binds to each GeneXus major.
- [Environment variables](environment_variables.md) — every runtime variable read by the Gateway and Worker.
- [Pinning the write destination](write-destination-pin.md) — fixing the target KB across restarts.
- [Human-in-the-loop elicitation](elicitation.md) — client-side KB picker and human approval for irreversible calls.
- [Migração para MCP 3.0](migration-3.0.md) — the 3.0 migration contract.
- [Nexus-IDE sharing guide](SHARING_GUIDE.md) — installing the VS Code extension and pointing clients at it.

## Using the tools

- [Capabilities inventory](mcp_capabilities_inventory.md) — the published MCP surface, tool by tool.
- [LLM playbook: AXI CLI + MCP](llm_cli_mcp_playbook.md) — how agents should combine the CLI and MCP calls.
- [AXI CLI contract](axi_cli_contract.md) — machine-facing contract of the `genexus-mcp` commands.
- [Object canonical JSON schema](object_json_schema.md) — the JSON view used by `mode:patch`.
- [Linter rules](linter_rules.md) — GeneXus linter rules and their documentation.

### Authoring guides

- [Textual Variables edits](variables-text-integrity.md) — validation and verification scope of Variables writes.
- [External Object methods](external-object-method-authoring.md) — `genexus_authoring action=add_external_method`.
- [Native Data View authoring](genexus_data_view.md) — `genexus_data_view`.
- [Typed Transaction records](transaction-records.md) — `genexus_db` record actions.
- [Moving a Transaction attribute](transaction_move_attribute.md) — `genexus_structure action=move_attribute`.
- [Removing a Transaction attribute](transaction_remove_attribute.md) — `genexus_edit mode=ops`.
- [Moving objects safely](object_move.md) — `genexus_properties action=move`.

### WorkWithPlus, patterns and K2B

- [Buttons and variable grids on an Empty WebPanel](wwp-empty-webpanel.md) — typed WorkWithPlus operations.
- [WorkWithPlus template objects](wwp-template-objects.md) — templates stored outside `PatternSettingsPart`.
- [Raw pattern XML property edits](pattern-xml-property-edits.md) — what raw `PatternInstance` edits may change.
- [K2B WebPanel Designer bridge](k2b-ide-bridge.md) — the opt-in `genexus_k2b_designer` IDE bridge.

## Contracts

- [Response envelope](envelope.md) and [envelope coverage](envelope-coverage.md).
- [Mutation and change-set contract](change-set-contract.md) — idempotency and logical operations.
- [KB isolation and local authorization](kb-isolation-contract.md).
- [Worker ownership and orphan cleanup](worker-ownership.md).
- [Capability release states](capability-release-states.md).
- [Tool identity registry](tool-identity-registry.md) and the generated
  [operation contract inventory](operation-contract-inventory.json).
- [Required Events save isolation](events-object-save-isolation.md) and the
  [restricted U16 Events save candidate](events-save-u16-candidate.md).

## Architecture and debugging

- [Technical architecture](technical_architecture.md) — the Gateway/Worker runtime.
- [MCP debugging guide](mcp_debugging_guide.md).
- [GeneXus 18 SDK discovery](sdk_gx18_discovery.md) — SDK bootstrapping and performance findings.
- [Performance baseline and targets](metrics-baseline.md) and the [benchmark reports](benchmarks/).
- [Shell-out security audit](security-audit-shell-outs-2026-05-24.md).

## Testing and validation

- [Live KB test harness](live-kb-test-harness.md) and the [live-KB validation matrix](live-kb-validation-matrix.md).
- [SDK CI validation lane](ci-sdk-validation.md).
- [Agent evaluation corpus](agent-evals.md).
- [Release build warning baseline](build_warning_baseline.md).
- [Record safety and performance evidence](transaction-records-benchmarks.md).

## Contributing, agents and releases

- [Agent playbook](agent_playbook.md) — SDK authoring order, placement and Windows gotchas.
- [Agent coordination](agent-coordination.md) — delegating implementation and review lanes.
- [Multi-PR review playbook](pr-review-playbook.md).
- [Release protocol](release_protocol.md) and the [release recovery pointer](RELEASE.md).

## Status and roadmaps

- [Limitations tracking](mcp_limitations_tracking.md) — evidence-backed status of each capability.
- [Execution backlog](mcp_execution_backlog.md).
- [IDE parity roadmap](mcp-roadmap-ide-parity.md), [SDK coverage-gap matrix](sdk_coverage_gap_matrix.md),
  [SDK endpoints roadmap](sdk_endpoints_roadmap.md) and
  [uncovered SDK endpoints](sdk_uncovered_endpoints_2026-07-20.md).
- [Nexus IDE roadmap](nexus-ide-roadmap.md) and [recon map](nexus-ide-recon.md).

## Design spikes (not shipping capabilities)

- [Typed visual authoring](typed-visual-authoring.md).
- [Deterministic conversion bundle](conversion-bundle.md).
- [Business Component adapter](business-component-adapter.md).

## Investigations and historical records

- [WWP PatternInstance investigation](wwp_pattern_investigation.md).
- [Pattern Settings SDK save audit](pattern-settings-sdk-audit.md) and
  [Pattern Settings templates (3.2.2 record)](pattern-settings-templates.md).
- [Team Development ignored objects](teamdev_commit_ignore_505.md).
- [Friction report 2026-05-22](mcp-improvements-2026-05-22.md).
- [3.0 integration evidence 2026-09-06](v3-integration-evidence-2026-09-06.md).
- [SDK probe artifacts](sdk-probe/README.md) — generated SDK surface maps and feasibility notes.

## Other directories

- [`plans/`](plans/) and [`superpowers/`](superpowers/) — dated implementation plans and design specs.
- [`issues/`](issues/) — issue investigation notes.
- [`schemas/`](schemas/) and [`examples/`](examples/) — JSON schemas and example payloads.
