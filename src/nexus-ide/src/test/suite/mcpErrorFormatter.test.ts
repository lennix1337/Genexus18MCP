import * as assert from "assert";
import { formatMcpErrorMessage, extractMcpError } from "../../utils/McpErrorFormatter";

suite("MCP error formatter", () => {
  test("preserves the unknown-outcome code and recovery instruction", () => {
    const payload = extractMcpError({ code: "outcome_unknown", operationKey: "edit-123" });
    assert.strictEqual(payload.code, "outcome_unknown");
    assert.match(
      formatMcpErrorMessage("Save failed:", payload),
      /Operation outcome is unknown.*Inspect the operation before retrying/,
    );
  });
});
