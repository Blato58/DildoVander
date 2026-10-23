using System.Buffers;

namespace Kufr.Backend.Features.CustomerData;

internal interface ICustomerMemoryGuardedDataSource : ICustomerDataMemoryAdmission
{
    CustomerMemoryDataSnapshot Capture(CustomerDataGuardScope scope);
    bool IsCurrent(CustomerDataGuardScope scope, CustomerMemoryDataSnapshot snapshot);
    void Release(CustomerDataGuardScope scope, CustomerMemoryDataSnapshot snapshot,
        ReadOnlyMemory<byte> payload, string payloadSha256, IBufferWriter<byte> output);
}

// Advance may already have accepted bytes. The HTTP owner must abort the
// response, never append an error document or describe the output as rolled back.
internal sealed class CustomerDataReleaseUncertainException(Exception inner)
    : Exception("customer_data_release_uncertain", inner);
