using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.CustomerData;

// Never await while this scope is alive: it owns the existing archive Monitor.
internal sealed class CustomerDataGuardScope : IDisposable
{
    private readonly CustomerDataGuardPrelude prelude;
    private readonly KeepsakeAccountExportGuard shared;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;
    private DateTimeOffset? downloadExpiresAt;

    internal CustomerOwnershipInventory Inventory { get; }
    internal ICustomerDataMemoryLease MemoryAdmission { get; }
    internal IReadOnlyList<CustomerKeepsakeExportMetadata> Keepsakes { get; }
    internal string KeepsakeRevision { get; }

    private CustomerDataGuardScope(CustomerDataGuardPrelude prelude, ICustomerDataMemoryLease memoryAdmission,
        KeepsakeAccountExportGuard shared, CustomerOwnershipInventory inventory,
        IReadOnlyList<CustomerKeepsakeExportMetadata> keepsakes, string keepsakeRevision)
    {
        this.prelude = prelude;
        this.shared = shared;
        Inventory = inventory;
        MemoryAdmission = memoryAdmission;
        Keepsakes = keepsakes;
        KeepsakeRevision = keepsakeRevision;
    }

    internal static CustomerDataGuardScope Create(CustomerDataGuardPrelude prelude,
        ICustomerDataMemoryLease memoryAdmission, KeepsakeAccountExportGuard shared,
        CustomerOwnershipInventory inventory, IReadOnlyList<CustomerKeepsakeExportMetadata> keepsakes,
        string keepsakeRevision)
    {
        return new CustomerDataGuardScope(prelude, memoryAdmission, shared, inventory, keepsakes, keepsakeRevision);
    }

    internal void ValidateCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireOwnerThread();
        prelude.ValidateCurrent();
        MemoryAdmission.ValidateCurrent();
        if (downloadExpiresAt is { } expiry) { prelude.ValidateDownloadExpiry(expiry); }
    }

    internal void ConstrainDownloadExpiry(DateTimeOffset expiresAt)
    {
        ValidateCurrent();
        downloadExpiresAt = downloadExpiresAt is { } current && current < expiresAt ? current : expiresAt;
        ValidateCurrent();
    }

    private void RequireOwnerThread()
    {
        if (ownerThread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Customer-data shared scope must remain on its acquiring thread.");
        }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        RequireOwnerThread();
        disposed = true;
        try { shared.Dispose(); }
        finally { prelude.ReleaseShared(); }
    }
}
