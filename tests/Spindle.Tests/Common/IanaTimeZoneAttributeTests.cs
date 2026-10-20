using Spindle.Api.Common;

namespace Spindle.Tests.Common;

public sealed class IanaTimeZoneAttributeTests
{
    [Theory]
    [InlineData("Europe/Zagreb")]
    [InlineData("America/New_York")]
    [InlineData("UTC")]
    public void Accepts_iana_ids(string id)
    {
        Assert.True(new IanaTimeZoneAttribute().IsValid(id));
    }

    [Theory]
    [InlineData("Central European Standard Time")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    public void Rejects_other_values(string id)
    {
        Assert.False(new IanaTimeZoneAttribute().IsValid(id));
    }

    [Fact]
    public void Leaves_missing_values_to_required()
    {
        Assert.True(new IanaTimeZoneAttribute().IsValid(null));
    }
}
