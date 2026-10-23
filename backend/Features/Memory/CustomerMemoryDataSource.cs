using Kufr.Backend.Features.CustomerData;

namespace Kufr.Backend.Features.Memory;

internal sealed class CustomerMemoryDataSource(
    ICustomerDataGuardAuthority authority,
    ICustomerMemoryGuardedDataSource guarded) : ICustomerMemoryDataSource
{
    public async Task<CustomerMemoryDataSnapshot> CaptureAsync(
        CustomerOwnershipInventory inventory,
        CancellationToken token = default)
    {
        using var prelude = await authority.AcquireAsync(inventory.Access, token);
        using var admission = await guarded.AcquireAsync(prelude.Deadline, token);
        using var scope = prelude.AcquireShared(admission);
        if (!MatchesInventory(scope.Inventory, inventory))
        {
            throw new CustomerDataAccessException(409, "customer_data_unavailable");
        }

        scope.ValidateCurrent();
        var snapshot = guarded.Capture(scope);
        scope.ValidateCurrent();
        return snapshot;
    }

    public async Task<bool> IsCurrentAsync(
        CustomerOwnershipInventory inventory,
        CustomerMemoryDataSnapshot snapshot,
        CancellationToken token = default)
    {
        try
        {
            using var prelude = await authority.AcquireAsync(inventory.Access, token);
            using var admission = await guarded.AcquireAsync(prelude.Deadline, token);
            using var scope = prelude.AcquireShared(admission);
            if (!MatchesInventory(scope.Inventory, inventory)) { return false; }

            scope.ValidateCurrent();
            var current = guarded.IsCurrent(scope, snapshot);
            scope.ValidateCurrent();
            return current;
        }
        catch (CustomerDataAccessException error) when (error.Status is 401 or 409)
        {
            return false;
        }
    }

    private static bool MatchesInventory(CustomerOwnershipInventory current,
        CustomerOwnershipInventory requested) =>
        current.Complete && requested.Complete
        && current.Revision == requested.Revision
        && current.Access == requested.Access
        && current.Sessions.SequenceEqual(requested.Sessions);
}
