# Business Component adapter spike

Status: **deferred pending a disposable generated application fixture**.

The v3 `genexus_db` record tools are a design-time, metadata-driven SQL adapter.
Their responses identify `dataAccess=typed_sql` and
`businessRulesExecuted=false`. They do not claim to execute a GeneXus
Transaction or Business Component.

A Business Component `Save` belongs to the generated application runtime. The
Worker hosts the GeneXus design-time SDK and currently has no authorized
application endpoint, generated assembly contract, runtime identity, or
isolated database fixture from which to prove BC behavior. A method signature
found by SDK reflection would not establish that rules, defaults, second-level
rows, messages, commit, and rollback execute correctly.

The next spike is intentionally constrained to one disposable .NET generator
application and one synthetic Transaction:

1. Generate an application with a controlled key, a negative-quantity `Error`
   rule, a default, and one second-level row.
2. Resolve the existing application endpoint and authentication boundary; do
   not create a new host or publish an application implicitly.
3. Record the real request/response shape for preview, invalid `Save`, valid
   `Save`, messages, generated keys, reread, and rollback.
4. Repeat the call after changing the environment or preview values and verify
   that the adapter rejects the stale target.

The spike is not runnable in this checkout because no generated application
fixture or authorized runtime endpoint is present. That is an evidence-based
blocker, not a reason to expose an unverified MCP tool. Until the fixture exists,
the only supported path is typed SQL with the explicit semantics above; a typed
SQL receipt never authorizes a Business Component call.
