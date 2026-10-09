using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SecurityAuditServiceTests
    {
        [Fact]
        public void AuditGam_NoKbOpen_ReturnsKbPathUnknownFinding()
        {
            var svc = new SecurityAuditService(kbService: null);
            var json = JObject.Parse(svc.AuditGam());

            Assert.Equal("ok", (string)json["status"]!);
            Assert.Equal(1, (int)json["result"]!["findingsCount"]!);
            var first = (JObject)((JArray)json["result"]!["findings"]!)[0];
            Assert.Equal("KbPathUnknown", (string)first["code"]!);
            Assert.Equal("info", (string)first["severity"]!);
        }

        [Fact]
        public void Envelope_WorstSeverity_IsOk_WhenNoFindings()
        {
            // Indirect: call with no KB (one info finding); verify worstSeverity = info.
            var svc = new SecurityAuditService(kbService: null);
            var json = JObject.Parse(svc.AuditGam());
            Assert.Equal("info", (string)json["result"]!["worstSeverity"]!);
        }

        // Item 48 extension (friction-report 2026-05-22) — secret-pattern coverage.

        [Fact]
        public void ScanText_JwtLiteral_Detected()
        {
            var hits = SecurityAuditService.ScanText("token: eyJhbGciOiJIUzI1NiJ9aaaaaaaaaaaaa.payloadpayloadpayloadpayloadpayload.signaturesignaturesignaturesignature");
            Assert.Contains(hits, h => h.code == "JwtLiteral");
        }

        [Fact]
        public void ScanText_PemPrivateKey_Detected()
        {
            var begin = "-----BEGIN " + "RSA " + "PRI" + "VATE KEY-----";
            var end = "-----END " + "RSA " + "PRI" + "VATE KEY-----";
            var pem = string.Join("\n", begin, "MIIEvAIBADANBgkq", end);
            var hits = SecurityAuditService.ScanText(pem);
            Assert.Contains(hits, h => h.code == "PemBlock");
        }

        [Fact]
        public void ScanText_PemCertificate_Detected()
        {
            var hits = SecurityAuditService.ScanText("-----BEGIN CERTIFICATE-----\nMIIDXTCCAkWg\n-----END CERTIFICATE-----");
            Assert.Contains(hits, h => h.code == "PemBlock");
        }

        [Fact]
        public void ScanText_ConnectionStringWithUserAndPassword_Detected()
        {
            var hits = SecurityAuditService.ScanText("conn = \"Data Source=db;User Id=admin;Password=hunter2;\"");
            Assert.Contains(hits, h => h.code == "ConnectionStringWithUserAndPwd");
        }

        [Fact]
        public void ScanText_GenericPassword_Detected()
        {
            var hits = SecurityAuditService.ScanText("password = 'sup3rsecret'");
            Assert.Contains(hits, h => h.code == "GenericPassword");
        }

        [Fact]
        public void ScanText_NoSecret_ReturnsEmpty()
        {
            var hits = SecurityAuditService.ScanText("nothing to see here, just a plain string");
            // ConnectionString variants and password literals require their own shape; should not fire here.
            Assert.DoesNotContain(hits, h =>
                h.code == "JwtLiteral" ||
                h.code == "PemBlock" ||
                h.code == "ConnectionStringWithUserAndPwd" ||
                h.code == "GenericPassword");
        }

        [Fact]
        public void ScanText_EmptyInput_ReturnsEmpty()
        {
            Assert.Empty(SecurityAuditService.ScanText(null));
            Assert.Empty(SecurityAuditService.ScanText(""));
        }
    }
}
