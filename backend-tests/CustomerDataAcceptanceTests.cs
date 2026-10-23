// The normal offline backend CI runner executes this group. Keep the focused
// switches available for diagnosis without leaving new boundaries untested in CI.
internal static class CustomerDataAcceptanceTests
{
    internal static async Task RunAsync()
    {
        await AccountDataExportAuthorityTests.RunAsync();
        await AccountDataExportStoreTests.RunAsync();
        await AccountDataExportDocumentTests.RunAsync();
        await AccountDataExportDownloadGuardTests.RunAsync();
        await AccountIdentityTests.RunAccountDataExportHttpAsync();
        await CustomerMemoryOriginTests.RunAsync();
        await CustomerMemoryReadPolicyTests.RunAsync();
        await CustomerMemoryWriteLeaseTests.RunAsync();
        await AccountIdentityTests.RunCustomerMemoryDataAsync();
        await AccountIdentityTests.RunCustomerMemoryMutationsAsync();
        await AccountIdentityTests.RunCustomerMemoryHttpAsync();
        await AccountIdentityTests.RunCustomerMemoryExportIntegrationAsync();
        await AccountIdentityTests.RunCustomerDataLockContentionAsync();
        await AccountIdentityTests.RunCustomerDataStartupAsync();
    }
}
