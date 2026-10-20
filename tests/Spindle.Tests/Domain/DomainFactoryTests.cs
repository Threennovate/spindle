using Spindle.Domain.Common;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;

namespace Spindle.Tests.Domain;

public sealed class DomainFactoryTests
{
    [Fact]
    public void Organization_name_is_trimmed()
    {
        var organization = Organization.Create("  Threennovate  ");

        Assert.Equal("Threennovate", organization.Name);
        Assert.NotEqual(Guid.Empty, organization.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Organization_requires_a_name(string name)
    {
        Assert.Throws<DomainValidationException>(() => Organization.Create(name));
    }

    [Fact]
    public void Organization_name_has_a_maximum_length()
    {
        Organization.Create(new string('a', Organization.NameMaxLength));

        Assert.Throws<DomainValidationException>(() => Organization.Create(new string('a', Organization.NameMaxLength + 1)));
    }

    [Fact]
    public void Workspace_requires_an_organization()
    {
        Assert.Throws<DomainValidationException>(() => Workspace.Create(Guid.Empty, "Workspace"));
    }

    [Fact]
    public void Memberships_require_a_user()
    {
        Assert.Throws<DomainValidationException>(() => OrganizationMember.Create(Guid.NewGuid(), Guid.Empty, OrganizationRole.Owner));
        Assert.Throws<DomainValidationException>(() => WorkspaceMember.Create(Guid.NewGuid(), Guid.Empty, WorkspaceRole.Admin));
    }

    [Fact]
    public void Each_entity_gets_its_own_id()
    {
        Assert.NotEqual(Organization.Create("A").Id, Organization.Create("A").Id);
    }
}
