using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TypeIntrospectServiceTests
    {
        private sealed class FakeDomain
        {
            public System.Collections.Generic.List<FakeEnumValue> EnumValues { get; } =
                new System.Collections.Generic.List<FakeEnumValue>();
        }

        private sealed class FakeEnumValue
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Description { get; set; }
        }

        [Fact]
        public void ComputeNumericRange_8_0_Unsigned_Is_99999999()
        {
            TypeIntrospectService.ComputeNumericRange(8, 0, signed: false,
                out decimal min, out decimal max);
            Assert.Equal(0m, min);
            Assert.Equal(99999999m, max);
        }

        [Fact]
        public void ComputeNumericRange_5_2_Signed_Is_999_99()
        {
            TypeIntrospectService.ComputeNumericRange(5, 2, signed: true,
                out decimal min, out decimal max);
            Assert.Equal(-999.99m, min);
            Assert.Equal(999.99m, max);
        }

        [Fact]
        public void ComputeNumericRange_8_2_Signed_Is_999999_99()
        {
            TypeIntrospectService.ComputeNumericRange(8, 2, signed: true,
                out decimal min, out decimal max);
            Assert.Equal(-999999.99m, min);
            Assert.Equal(999999.99m, max);
        }

        [Fact]
        public void ValidateValue_Numeric_Overflow_IsInvalid()
        {
            var info = new TypeIntrospectService.DomainInfo
            {
                Name = "Money",
                Kind = "Domain",
                BaseType = "Numeric",
                Length = 5,
                Decimals = 2,
                Signed = false
            };
            var r = TypeIntrospectService.ValidateValue(info, "12345.67");
            Assert.False((bool)r["valid"]);
            Assert.NotNull(r["reason"]);
        }

        [Fact]
        public void ValidateValue_Numeric_Within_IsValid()
        {
            var info = new TypeIntrospectService.DomainInfo
            {
                Name = "Pct",
                Kind = "Domain",
                BaseType = "Numeric",
                Length = 5,
                Decimals = 2,
                Signed = true
            };
            var r = TypeIntrospectService.ValidateValue(info, "-12.34");
            Assert.True((bool)r["valid"]);
        }

        [Fact]
        public void ValidateValue_Character_Overflow_IsInvalid()
        {
            var info = new TypeIntrospectService.DomainInfo
            {
                Name = "ShortName",
                Kind = "Domain",
                BaseType = "Character",
                Length = 5
            };
            var r = TypeIntrospectService.ValidateValue(info, "way too long");
            Assert.False((bool)r["valid"]);
        }

        [Fact]
        public void ValidateValue_UnknownBaseType_Graceful()
        {
            var info = new TypeIntrospectService.DomainInfo
            {
                Name = "Weird",
                Kind = "Domain",
                BaseType = "Quaternion"
            };
            var r = TypeIntrospectService.ValidateValue(info, "anything");
            Assert.True((bool)r["valid"]);
            Assert.NotNull(r["note"]);
            Assert.Contains("Quaternion", (string)r["note"]);
        }

        [Fact]
        public void ValidateValue_AllowedValues_Mismatch_IsInvalid()
        {
            var info = new TypeIntrospectService.DomainInfo
            {
                Name = "Status",
                Kind = "Domain",
                BaseType = "Character",
                Length = 1,
                AllowedValues = new System.Collections.Generic.List<TypeIntrospectService.DomainEnumValueRecord>
                {
                    new TypeIntrospectService.DomainEnumValueRecord { Name = "Active", Value = "A" },
                    new TypeIntrospectService.DomainEnumValueRecord { Name = "Inactive", Value = "I" }
                }
            };
            var r = TypeIntrospectService.ValidateValue(info, "X");
            Assert.False((bool)r["valid"]);
            Assert.NotNull(r["hint"]);
        }

        [Fact]
        public void ReadDomainEnumValues_UsesDirectSdkEnumerationShape()
        {
            var domain = new FakeDomain();
            domain.EnumValues.Add(new FakeEnumValue
            {
                Name = "Queued",
                Value = "1",
                Description = "Waiting"
            });

            var values = TypeIntrospectService.ReadDomainEnumValues(domain);

            var value = Assert.Single(values);
            Assert.Equal("Queued", value.Name);
            Assert.Equal("1", value.Value);
            Assert.Equal("Waiting", value.Description);
        }

        [Fact]
        public void Run_UnknownAction_ReturnsError()
        {
            var svc = new TypeIntrospectService(null, null);
            var json = svc.Run(new JObject { ["action"] = "bogus" });
            var o = JObject.Parse(json);
            Assert.Equal("error", (string)o["status"]);
            Assert.Equal("InvalidAction", (string)o["error"]?["code"]);
        }

        [Fact]
        public void Run_DescribeMissingDomain_ReturnsTypeNotFound()
        {
            var svc = new TypeIntrospectService(null, null);
            var json = svc.Run(new JObject { ["action"] = "describe", ["name"] = "MissingDomain" });
            var o = JObject.Parse(json);
            Assert.Equal("error", (string)o["status"]);
            Assert.Equal("TypeNotFound", (string)o["error"]?["code"]);
            Assert.Contains("MissingDomain", (string)o["error"]?["message"]);
        }

        [Fact]
        public void Run_ListAction_WithoutKb_ReturnsEmptySuccess()
        {
            var svc = new TypeIntrospectService(null, null);
            var json = svc.Run(new JObject { ["action"] = "list" });
            var o = JObject.Parse(json);
            Assert.Equal("ok", (string)o["status"]);
            Assert.Equal(0, (int)o["result"]["count"]);
        }
    }
}
