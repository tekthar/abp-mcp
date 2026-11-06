using Volo.Abp.Authorization.Permissions;

namespace AbpMcp.Sample.Library;

/// <summary>The Library sample's permissions. Only deleting a member needs one.</summary>
public static class LibraryPermissions
{
    /// <summary>The permission group.</summary>
    public const string GroupName = "Library";

    /// <summary>Delete a member (<c>Member_Delete</c>).</summary>
    public const string MembersDelete = GroupName + ".Members.Delete";
}

/// <summary>Defines <see cref="LibraryPermissions"/> so ABP's permission checker (and abp-mcp) know them.</summary>
public sealed class LibraryPermissionDefinitionProvider : PermissionDefinitionProvider
{
    /// <inheritdoc />
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(LibraryPermissions.GroupName);
        group.AddPermission(LibraryPermissions.MembersDelete);
    }
}
