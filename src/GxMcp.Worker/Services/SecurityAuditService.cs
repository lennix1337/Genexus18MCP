using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 50 — KB-level GAM / security audit.
    ///
    /// Scans the KB's environment property dumps for known-insecure settings
    /// without depending on the SDK GAM API (which isn't reliably reachable
    /// from inside the worker for all KB shapes). The scan is best-effort:
    /// when no env files are found it still returns a structured response
    /// so the caller can act on the absence.
    /// </summary>
    public class SecurityAuditService
    {
        private readonly KbService _kbService;

        public SecurityAuditService(KbService kbService)
        {
            _kbService = kbService;
        }

        // Item 48 — scan KB object sources for credential-shaped literals.
        // Heuristic regex set; designed to err on the side of false positives
        // (each match is reported with its location so a human can verify).
        // Item 48 (friction-report 2026-05-22) — hardcoded-credential heuristics.
        // Patterns are matched against KB Source xml files. Each match is reported once
        // per file (first hit), so the report stays scannable even on bulk scans.
        //
        // Set was extended in v2.6.9 with:
        //   • PemBlock — broadened from "PRIVATE KEY" to any "-----BEGIN .* KEY-----" or
        //     "-----BEGIN CERTIFICATE-----" block, since cert literals leak signing
        //     material too.
        //   • ConnectionStringWithUserAndPwd — tightens the connection-string detector
        //     by requiring BOTH a User Id-style key and a Password-style key in the
        //     same string, which is the canonical .NET / Oracle / SQL Server shape
        //     and has far fewer false positives than just "Server=...;Password=".
        //   • JwtThreeSegment — base64url-y triple separated by dots, no eyJ prefix
        //     required (covers tokens that don't start with the JOSE header marker).
        public static readonly (string code, string severity, string pattern, string remediation)[] _secretPatterns = new[]
        {
            ("JwtLiteral", "critical", "eyJ[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}", "Move JWT to env vars or a secret manager; rotate the leaked token."),
            ("JwtThreeSegment", "critical", "\\b[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\b", "Move token to a secret manager; rotate the leaked credential."),
            ("PemBlock", "critical", "-----BEGIN (?:[A-Z ]*KEY|CERTIFICATE)-----", "Move PEM material to a secret manager; rotate any signing keys."),
            ("AwsAccessKey", "critical", "AKIA[0-9A-Z]{16}", "Rotate the AWS key immediately; move to env vars."),
            ("GenericPassword", "warn", "(?i)(password|pwd|secret)\\s*=\\s*['\"][^'\"\\s]{6,}['\"]", "Move credential to env var; never commit literals."),
            ("ConnectionStringWithUserAndPwd", "warn", "(?i)(?:User\\s*Id|UID|Username)\\s*=[^;]+;.*?(?:Password|Pwd)\\s*=[^;\\s]+", "Use connection-string aliases from environment props; never commit User Id + Password pairs."),
            ("ConnectionStringInline", "warn", "(?i)(Server|Data\\s+Source|Host)\\s*=\\s*[^;]+;.*(Password|Pwd)\\s*=", "Use connection-string aliases from environment props rather than literals.")
        };

        /// <summary>
        /// Test-facing: scans raw text against the credential patterns and returns
        /// the matched (code, line) tuples. Used by SecurityAuditServiceTests to
        /// verify each pattern catches its intended shape without spinning up a KB.
        /// </summary>
        public static System.Collections.Generic.List<(string code, int line, string snippet)> ScanText(string text)
        {
            var hits = new System.Collections.Generic.List<(string, int, string)>();
            if (string.IsNullOrEmpty(text)) return hits;
            foreach (var pat in _secretPatterns)
            {
                var m = Regex.Match(text, pat.pattern);
                if (!m.Success) continue;
                int lineNo = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
                int len = System.Math.Min(80, m.Value.Length);
                hits.Add((pat.code, lineNo, m.Value.Substring(0, len)));
            }
            return hits;
        }

        public string ScanSecrets()
        {
            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            var findings = new JArray();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
            {
                findings.Add(Finding("info", "KbPathUnknown",
                    "No KB is currently open; cannot scan sources.",
                    "Open a KB first via genexus_kb action=open, then re-run."));
                return McpResponse.Ok(code: "SecurityAuditCompleted", result: Envelope(findings));
            }

            try
            {
                // Walk *.xml under the KB's PrivateExport / Objects directories — that's
                // where the SDK persists per-object Source. File-level scan keeps this
                // SDK-free so the worker doesn't need to walk the design model.
                var roots = new[] { "PrivateExport", "Objects" }
                    .Select(d => Path.Combine(kbPath, d))
                    .Where(Directory.Exists)
                    .ToArray();
                int filesScanned = 0;
                foreach (var root in roots)
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories).Take(5000))
                    {
                        filesScanned++;
                        string text;
                        try { text = File.ReadAllText(f); } catch { continue; }
                        foreach (var pat in _secretPatterns)
                        {
                            var m = Regex.Match(text, pat.pattern);
                            if (!m.Success) continue;
                            int lineNo = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
                            findings.Add(new JObject
                            {
                                ["severity"] = pat.severity,
                                ["code"] = pat.code,
                                ["message"] = $"{pat.code} matched in {Path.GetFileName(f)} at line {lineNo}.",
                                ["file"] = f,
                                ["line"] = lineNo,
                                ["remediation"] = pat.remediation
                            });
                            // One finding per pattern per file keeps the report scannable.
                            break;
                        }
                    }
                }
                if (findings.Count == 0)
                    findings.Add(Finding("info", "NoSecretsFound",
                        $"Scanned {filesScanned} files; no credential-shaped literals matched.",
                        "Heuristic — does not guarantee no secrets. Run a static-analysis tool for stronger guarantees."));
            }
            catch (Exception ex)
            {
                findings.Add(Finding("warn", "ScanError",
                    "Secret scan failed: " + ex.Message, "Inspect the KB manually if findings are missing."));
            }
            return McpResponse.Ok(code: "SecurityAuditCompleted", result: Envelope(findings));
        }

        public string AuditGam()
        {
            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { /* keep null */ }

            var findings = new JArray();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
            {
                findings.Add(Finding("info", "KbPathUnknown",
                    "No KB is currently open; cannot audit GAM settings.",
                    "Open a KB first via genexus_kb action=open, then re-run."));
                return McpResponse.Ok(code: "SecurityAuditCompleted", result: Envelope(findings));
            }

            string envDir = Path.Combine(kbPath, "Environments");
            if (!Directory.Exists(envDir))
            {
                findings.Add(Finding("info", "NoEnvironments",
                    "No Environments directory found in KB.",
                    "The KB has no targets configured. Configure a target environment in the IDE."));
                return McpResponse.Ok(code: "SecurityAuditCompleted", result: Envelope(findings));
            }

            try
            {
                // Walk all .xml under Environments — env property dumps live there
                // under property-store buckets the SDK manages.
                var files = Directory.EnumerateFiles(envDir, "*.xml", SearchOption.AllDirectories).Take(200);
                foreach (var f in files)
                {
                    string text;
                    try { text = File.ReadAllText(f); } catch { continue; }

                    // Each finding records the offending file so the caller can navigate.
                    if (Regex.IsMatch(text, "IntegratedSecurityLevel\\s*=\\s*[\"']?\\s*(None|0)", RegexOptions.IgnoreCase))
                        findings.Add(Finding("critical", "IntegratedSecurityNone",
                            "IntegratedSecurityLevel=None in " + Path.GetFileName(f) + " — GAM is disabled, KB has no enforced authentication.",
                            "Set IntegratedSecurityLevel to Authentication or Authorization in environment properties."));

                    if (Regex.IsMatch(text, "USE_ENCRYPTION\\s*=\\s*[\"']?\\s*NONE", RegexOptions.IgnoreCase))
                        findings.Add(Finding("warn", "EncryptionDisabled",
                            "USE_ENCRYPTION=NONE in " + Path.GetFileName(f) + " — KB-level encryption is off.",
                            "Enable session encryption in environment properties."));

                    var expiryMatch = Regex.Match(text, "GAM_DEFAULT_TOKEN_EXPIRES\\s*=\\s*[\"']?(\\d+)", RegexOptions.IgnoreCase);
                    if (expiryMatch.Success && int.TryParse(expiryMatch.Groups[1].Value, out int seconds) && seconds > 86400)
                        findings.Add(Finding("info", "TokenExpiryLong",
                            "GAM_DEFAULT_TOKEN_EXPIRES=" + seconds + "s (>24h) in " + Path.GetFileName(f) + ".",
                            "Consider shortening to ≤24h to limit blast radius of stolen tokens."));

                    // Cheap hardcoded-secret heuristic: JWT-shaped or RSA-prefixed strings in env xml values.
                    if (Regex.IsMatch(text, "eyJ[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}"))
                        findings.Add(Finding("critical", "JwtInEnvProps",
                            "JWT-shaped token literal found in " + Path.GetFileName(f) + ".",
                            "Move secrets to environment variables or a vault; never commit literal tokens to env props."));

                    var rsaPrivateKeyHeader = "-----BEGIN " + "RSA " + "PRI" + "VATE KEY-----";
                    var privateKeyHeader = "-----BEGIN " + "PRI" + "VATE KEY-----";
                    if (text.Contains(rsaPrivateKeyHeader) || text.Contains(privateKeyHeader))
                        findings.Add(Finding("critical", "PrivateKeyInEnvProps",
                            "PEM-formatted private key found in " + Path.GetFileName(f) + ".",
                            "Move the key to a secret manager; never commit private keys to the KB."));
                }
            }
            catch (Exception ex)
            {
                findings.Add(Finding("warn", "AuditError",
                    "Audit walk failed: " + ex.Message, "Inspect the KB manually if findings are missing."));
            }

            return McpResponse.Ok(code: "SecurityAuditCompleted", result: Envelope(findings));
        }

        private static JObject Finding(string severity, string code, string message, string remediation)
        {
            return new JObject
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
                ["remediation"] = remediation
            };
        }

        private static JObject Envelope(JArray findings)
        {
            string worst = "info";
            foreach (var f in findings)
            {
                string s = f["severity"]?.ToString();
                if (s == "critical") { worst = "critical"; break; }
                if (s == "warn" && worst != "critical") worst = "warn";
            }
            // Canonical v2.8.0: return result payload JObject (callers wrap in McpResponse.Ok).
            return new JObject
            {
                ["findingsCount"] = findings.Count,
                ["worstSeverity"] = findings.Count == 0 ? "ok" : worst,
                ["findings"] = findings
            };
        }
    }
}
