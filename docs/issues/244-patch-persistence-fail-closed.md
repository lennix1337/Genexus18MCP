# Issue #244 — fail-closed text patch persistence

Reference: [lennix1337/Genexus18MCP#244](https://github.com/lennix1337/Genexus18MCP/issues/244)

## Problem

`genexus_edit` in `mode=patch` could report a successful save after the SDK
returned an object whose source was empty or stale. The verification path could
also turn an error envelope into an empty source, so the caller could not
distinguish a complete persisted part from an unavailable read. A repeated
patch could then match stale in-memory content and overwrite a production
source unintentionally.

The examples in this document are intentionally generic. No knowledge-base
path, credential, connection string, or production source is included.

## Safety contract

- Every text patch, including `dryRun=true`, takes its complete preflight read
  through the uncached verification reader. A short-lived read cache cannot be
  used as the base for a write or as evidence for a preview.
- A post-save verification read must be complete, non-truncated, error-free,
  and contain a string `source`/`content` value. A valid empty string remains
  valid only when the requested operation intentionally replaces the complete
  part with empty content.
- The pre-write read uses the same complete-source guard. A truncated,
  malformed, error, serialized/projection, or non-text source is rejected
  before any write is attempted.
- This patch guard applies to textual `mode=patch` edits. Full XML-backed
  writes retain their existing serialized-part support and verify the complete
  XML after save; projected virtual parts remain non-editable.
- If the SDK cache cannot be invalidated, or the SDK returns the same in-memory
  object for the requested fresh read, the patch fails with the stable
  `PatchReadFailed` code and includes `readCode: FreshReadUnavailable`. It does
  not claim persistence and does not silently fall back to the pre-write object.
- If the SDK save returns but the complete post-save read is unavailable, the
  operation returns `WriteVerificationUnavailable` with
  `saveAttempted:true`, `saved:false`, `verified:false`, `persisted:false`, and
  an explicit recovery hint. No automatic rollback is attempted against an
  unknown post-save state, and the target remains marked dirty for the next
  build. `saved:true` is reserved for a verified result.
- Confirmed responses expose the complete source returned by the independent
  read, rather than echoing the requested patch payload.
- An explicitly requested rollback uses the version token observed after the
  failed write as a concurrency fence. If that token is unavailable, the MCP
  reports that rollback was not attempted instead of overwriting a concurrent
  edit. PatternInstance verification also refreshes the resolved WorkWithPlus
  child, not only the parent object.
- No Specify, Generate, Build, Rebuild, Reorg, publish, execution, or other
  lifecycle operation is invoked implicitly by this verification path.

## Regression coverage

The focused Worker suite covers:

- normal and comment-only patch receipts;
- divergence and intentional complete-source deletion;
- the `saved` versus `saveAttempted` contract for unverified writes;
- source/error/truncation guards;
- fresh-read and fail-closed wiring by convention tests.
- rollback version fencing and fresh PatternInstance-child resolution.

Run the focused suite from a checkout with a compatible GeneXus SDK:

```powershell
$env:GX_PATH = 'C:\Path\To\GeneXus18'
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj `
  --filter "FullyQualifiedName~PatchTextEditorTests|FullyQualifiedName~CommentOnlyPatchTests|FullyQualifiedName~PatchSafetyGuardTests"
```

The dirty-state regression is covered by `WriteDirtyOutcomeTests` in the same
Worker test project.

The full CI-equivalent preflight remains required before publishing a pull
request. Live KB validation is separate evidence and is not implied by these
unit tests.
