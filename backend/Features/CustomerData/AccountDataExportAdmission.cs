namespace Kufr.Backend.Features.CustomerData;

// The memory owner implements this with its existing localWrites semaphore.
// Admission is awaited before entering the thread-affine keepsake Monitor.
internal interface ICustomerDataMemoryAdmission
{
    string DatabasePath { get; }
    Task<ICustomerDataMemoryLease> AcquireAsync(CustomerDataGuardDeadline deadline,
        CancellationToken token = default);
}

internal interface ICustomerDataMemoryLease : IDisposable
{
    void ValidateCurrent();
}
