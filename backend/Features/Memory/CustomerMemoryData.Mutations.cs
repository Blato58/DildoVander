using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Memory;

internal sealed partial class CustomerMemoryData
{
    internal CustomerMemoryMutationResult Correct(
        CustomerDataGuardScope scope,
        string id,
        CustomerMemoryCorrectionRequest request)
    {
        var guard = RequireGuard(scope);
        var commitAttempted = false;
        var capacityRejected = false;
        CustomerMemoryMutationResult? mutation = null;

        try
        {
            using (EnterMemoryGate(scope))
            using (var database = OpenMemoryDatabase())
            {
                scope.ValidateCurrent();
                var result = store.CorrectCustomerMemory(
                    database.Connection, database.Transaction, scope.Inventory, id, request);

                scope.ValidateCurrent();
                guard.ValidateCurrent();
                commitAttempted = true;
                database.Transaction.Commit();

                if (result.CapacityRejected)
                {
                    capacityRejected = true;
                }
                else
                {
                    mutation = result.Mutation
                        ?? throw new InvalidOperationException("Committed customer-memory correction has no receipt.");
                    service.ClearCache();
                }
            }
        }
        catch (Exception error) when (commitAttempted)
        {
            throw new CustomerMemoryMutationUncertainException(error);
        }

        if (capacityRejected)
        {
            throw new CustomerDataAccessException(413, "archive_capacity");
        }

        return mutation
            ?? throw new InvalidOperationException("Customer-memory correction returned no mutation receipt.");
    }

    internal CustomerMemoryMutationResult Forget(
        CustomerDataGuardScope scope,
        string id,
        CustomerMemoryForgetRequest request)
    {
        var guard = RequireGuard(scope);
        var commitAttempted = false;
        CustomerMemoryMutationResult? mutation = null;

        try
        {
            using (EnterMemoryGate(scope))
            using (var database = OpenMemoryDatabase())
            {
                scope.ValidateCurrent();
                mutation = store.ForgetCustomerMemory(
                    database.Connection, database.Transaction, scope.Inventory, id, request);

                scope.ValidateCurrent();
                guard.ValidateCurrent();
                commitAttempted = true;
                database.Transaction.Commit();
                service.ClearCache();
            }
        }
        catch (Exception error) when (commitAttempted)
        {
            throw new CustomerMemoryMutationUncertainException(error);
        }

        return mutation
            ?? throw new InvalidOperationException("Customer-memory forget returned no mutation receipt.");
    }
}

// Commit was attempted but a later step failed. The outcome may be uncertain;
// the caller must not report rollback or retry as though the mutation were absent.
internal sealed class CustomerMemoryMutationUncertainException(Exception inner)
    : Exception("customer_memory_mutation_uncertain", inner);
