using GxMcp.Worker.Parsers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A Transaction attribute based on a Domain was read back as its primitive
    // type (`Qty : NUMERIC(6)`), so a write of `Qty : MyDomain` that the SDK had
    // persisted failed its own round-trip check and reported WriteNotPersisted.
    public class TransactionDslDomainReadTests
    {
        public class FakeDomain
        {
            public string Name { get; set; }
        }

        public class FakeAttribute
        {
            public FakeDomain DomainBasedOn { get; set; }
        }

        [Fact]
        public void DomainBasedAttribute_ReadsAsTheDomainName()
        {
            var attribute = new FakeAttribute { DomainBasedOn = new FakeDomain { Name = "McpEv3Dom" } };

            Assert.Equal("McpEv3Dom", TransactionDslParser.RenderAttributeType(attribute, "NUMERIC(6)"));
        }

        [Fact]
        public void PrimitiveAttribute_KeepsItsType()
        {
            Assert.Equal("VARCHAR(40)", TransactionDslParser.RenderAttributeType(new FakeAttribute(), "VARCHAR(40)"));
            Assert.Equal("NUMERIC(4)", TransactionDslParser.RenderAttributeType(null, "NUMERIC(4)"));
        }
    }
}
