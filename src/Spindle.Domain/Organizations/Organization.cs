using Spindle.Domain.Common;

namespace Spindle.Domain.Organizations;

/// <summary>
/// The customer: owns the members and one or more workspaces.
/// </summary>
public sealed class Organization : Entity
{
    public const int NameMaxLength = 140;

    private Organization()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public static Organization Create(string name) => new() { Name = Guard.Name(name, NameMaxLength) };

    public void Rename(string name) => Name = Guard.Name(name, NameMaxLength);
}
