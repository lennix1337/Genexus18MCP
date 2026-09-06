type McpErrorPayload = {
  code?: string;
  error?: string;
  status?: string;
  target?: string;
  part?: string;
  details?: string;
  objectName?: string;
  objectType?: string;
  availableParts?: string[];
};

function tryParseErrorMessage(message: string): McpErrorPayload {
  const normalized = message.startsWith("Error: ")
    ? message.substring("Error: ".length)
    : message;

  try {
    const parsed = JSON.parse(normalized) as McpErrorPayload;
    if (parsed && typeof parsed === "object") {
      return parsed;
    }
  } catch {
    // Fall back to raw error text when it is not JSON.
  }

  return { error: normalized };
}

export function extractMcpError(rawError: unknown): McpErrorPayload {
  if (rawError && typeof rawError === "object") {
    const candidate = rawError as McpErrorPayload;
    if (
      typeof candidate.code === "string" ||
      typeof candidate.error === "string" ||
      typeof candidate.details === "string" ||
      typeof candidate.target === "string"
    ) {
      return candidate;
    }
  }

  if (rawError instanceof Error) {
    return tryParseErrorMessage(rawError.message);
  }

  if (typeof rawError === "string") {
    return tryParseErrorMessage(rawError);
  }

  return { error: String(rawError) };
}

export function formatMcpErrorMessage(prefix: string, rawError: unknown): string {
  const payload = extractMcpError(rawError);
  const baseMessage = payload.error ||
    (payload.code === "outcome_unknown" ? "Operation outcome is unknown" : "Unknown failure");
  const segments = [prefix, baseMessage];

  if (payload.target) {
    segments.push(`Object: ${payload.target}`);
  }

  if (payload.part) {
    segments.push(`Part: ${payload.part}`);
  }

  if (payload.objectType && payload.objectName) {
    segments.push(`Resolved object: ${payload.objectType} ${payload.objectName}`);
  }

  if (payload.details) {
    segments.push(payload.details);
  }

  if (payload.code === "outcome_unknown") {
    segments.push("Inspect the operation before retrying");
  }

  if (Array.isArray(payload.availableParts) && payload.availableParts.length > 0) {
    segments.push(`Available parts: ${payload.availableParts.join(", ")}`);
  }

  return segments.join(" | ");
}
