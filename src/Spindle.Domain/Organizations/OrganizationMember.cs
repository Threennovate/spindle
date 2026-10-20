using Spindle.Domain.Common;

namespace Spindle.Domain.Organizations;

public enum OrganizationRole
{
    Owner,
    Admin,
    Member,
}

public sealed class OrganizationMember : Entity
{
    private OrganizationMember()
    {
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public OrganizationRole Role { get; private set; }

    public static OrganizationMember Create(Guid organizationId, Guid userId, OrganizationRole role) => new()
    {
        OrganizationId = Guard.Id(organizationId),
        UserId = Guard.Id(userId),
        Role = role,
    };

    public void ChangeRole(OrganizationRole role) => Role = role;
}
