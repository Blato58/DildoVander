namespace Kufr.Backend.Features.Identity;

internal static class AccountDevicePermissionPolicy
{
    internal const string ResourceType = "device";
    internal const string Operate = "operate";
    internal const string Manage = "manage";

    internal static bool AllowsOperation(IReadOnlyCollection<string> roles, bool hasDeviceGrant)
        => roles.Contains(AccountRoles.Owner) || roles.Contains(AccountRoles.Administrator)
            || roles.Contains(AccountRoles.Operator) && hasDeviceGrant;
}
