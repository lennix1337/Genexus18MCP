using System;
using Artech.Genexus.Common;
using GxMcp.Worker.Parsers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A new SDT member typed by a Domain ("Code : MyDomain") is created with AddItem(name,
    // eDBType) before it is bound to the Domain. A Domain name is not an eDBType member, so
    // the member needs the Domain's own primitive type, or it is never added.
    public class SdtDomainMemberTypeTests
    {
        private class FakeKbObject
        {
            public Guid Type => Guid.Empty;
        }

        // The SDK Domain hides KBObject.Type (a Guid) with `new eDBType Type`.
        private class FakeDomain : FakeKbObject
        {
            public new eDBType Type => eDBType.NUMERIC;
        }

        private class FakeDomainWithDataType
        {
            public eDBType DataType => eDBType.DATE;
        }

        [Fact]
        public void DomainDbType_ReadsTheHidingTypeProperty()
        {
            Assert.Equal(eDBType.NUMERIC, SdtDslParser.DomainDbType(typeof(eDBType), new FakeDomain()));
        }

        [Fact]
        public void DomainDbType_PrefersDataTypeWhenPresent()
        {
            Assert.Equal(eDBType.DATE, SdtDslParser.DomainDbType(typeof(eDBType), new FakeDomainWithDataType()));
        }

        [Fact]
        public void DomainDbType_FallsBackToVarcharWhenTheDomainExposesNoType()
        {
            Assert.Equal(eDBType.VARCHAR, SdtDslParser.DomainDbType(typeof(eDBType), new object()));
            Assert.Null(SdtDslParser.DomainDbType(null, new FakeDomain()));
        }
    }
}
