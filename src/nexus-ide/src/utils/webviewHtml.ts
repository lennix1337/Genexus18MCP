import * as crypto from "crypto";
import { escapeHtml } from "./htmlEscape";

/** Per-render CSP nonce for the webview's own inline <script>. */
export function createNonce(): string {
  return crypto.randomBytes(16).toString("base64");
}

/**
 * CSP for webviews that carry one nonce'd inline script and inline styles.
 * Scripts need the nonce, so inline event-handler attributes (onclick=...) are
 * blocked: wire handlers with addEventListener inside the nonce'd script.
 */
export function scriptNonceCsp(cspSource: string, nonce: string): string {
  return `default-src 'none'; style-src ${cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';`;
}

/** A static, script-less page (loading/error) with the given text, HTML-escaped. */
export function messagePage(text: unknown): string {
  return `<!DOCTYPE html>
<html>
<head>
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
</head>
<body><h1>${escapeHtml(text)}</h1></body>
</html>`;
}
