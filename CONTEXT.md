# CONTEXT.md — Genexus18MCP Domain Glossary

This document records the authoritative domain terms and concepts for Genexus18MCP. It defines the vocabulary for seams, modules, and domain boundaries.

## Core Knowledge Base Domain

- **Knowledge Base (KB)**: The root database directory hosting the GeneXus data model, objects, environments, versions, and SDK state.
- **KBObject**: A single design-time entity in a Knowledge Base (Transaction, Procedure, WebPanel, SDPanel, Table, Domain, DataSelector, DesignSystem, etc.).
- **KBObjectPart**: A strongly-typed sub-part of a KBObject (Source, Variables, WebForm, Layout, Structure, Rules, Events, Conditions, PatternInstance, etc.).
- **Environment**: A deployment and generation configuration defining the target database, model, web root path, and active generator (e.g. C#, Java).
- **Environment Scope**: An activation context that temporarily switches the KB's active model/environment for build execution and guarantees automatic LIFO restoration upon completion, cancellation, or error.

## Mutation & Authoring Domain

- **Mutation**: An atomic, verified modification to one or more parts of one or more KBObjects.
- **Mutation Request**: The declarative specification of an edit, patch, semantic operation, or batch authoring action.
- **Mutation Plan**: An in-memory preview of a mutation (including unified diffs, schema validation, touched objects, and broken reference detection) produced during dry-run simulation without touching the SDK COM state.
- **Unit of Work**: In-memory staging of multi-object modifications with coordinated validation and automatic reverse-order (LIFO) compensation rollback on failure.
- **SDK Object Writer**: An internal, STA-confined adapter responsible solely for applying prepared in-memory parts to native GeneXus SDK COM objects and calling `EnsureSave`.

## Visual Surface Domain

- **Visual Surface**: An interactive visual canvas and layout part of a KBObject (WebForm, Procedure Report Layout, or Design System Tokens/Styles).
- **Visual Surface Adapter**: An adapter responsible for projecting native SDK visual parts into canonical visual XML/DOM, calculating baseline deltas, and enforcing semantic color equivalence.
- **Baseline Preservation**: The preservation of untouched controls, coordinates, and properties across print blocks or web forms during partial layout edits.
- **Semantic Color Equivalence**: Value equality matching across color tokens (.NET strings, GeneXus RGB tokens, CSS rgb/rgba, hex codes, and named colors) preventing false-positive write verification mismatches.

## Inspection & Serialization Domain

- **Part Serializer**: A dedicated adapter responsible for serializing a specific KBObjectPart type to/from its external representation (e.g., text, XML, JSON).
- **Part Serializer Registry**: The registry of active, typed part serializers used during object inspection and read pagination.
- **Query Grammar**: The authoritative token parser and type normalizer resolving query prefixes (`type:`, `parent:`, `usedby:`, `metadata:`) and type aliases across search and list operations.

## Gateway Request Domain

- **Request Core**: `Program.ProcessMcpRequestCore` (`Program.RequestLoop.cs`) handles one JSON-RPC request: protocol handshake, session and KB resolution, then `tools/call` dispatch through `Program.DispatchToolCallCoreAsync` (`Program.ToolDispatch.cs`), which validates arguments, consults the semantic cache, routes to the worker or a gateway tool, and shapes the response. It is plain procedural code; there is no pluggable middleware chain.
- **Idempotency Middleware**: `IdempotencyMiddleware` wraps only the mutating tool dispatch inside the Request Core (keyed replay through the idempotency cache and journal). It is the single stage of its kind.
