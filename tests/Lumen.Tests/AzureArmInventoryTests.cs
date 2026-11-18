using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Providers;
using Xunit;

namespace Lumen.Tests;

public sealed class AzureArmInventoryTests
{
    [Fact]
    public void ArmConfigurationRejectsUnsafeSegmentsAndTenantIds()
    {
        Assert.Throws<ArgumentException>(() => new AzureArmConfiguration("00000000-0000-0000-0000-000000000000", "rg", "account"));
        Assert.Throws<ArgumentException>(() => new AzureArmConfiguration(Guid.NewGuid().ToString("D"), "../rg", "account"));
        Assert.Throws<ArgumentException>(() => new AzureArmConfiguration(Guid.NewGuid().ToString("D"), "rg", "account?bad"));
        var arm = new AzureArmConfiguration(Guid.NewGuid().ToString("D"), "rg", "account");
        Assert.Equal("rg", arm.ResourceGroup);
        Assert.Equal("account", arm.AccountName);
        Assert.Equal("https://management.azure.com/subscriptions/" + arm.SubscriptionId
            + "/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/account/deployments?api-version=2025-06-01",
            AzureArmInventory.BuildListUri(arm).AbsoluteUri);
    }

    [Fact]
    public async Task DeviceCodeInventoryRejectsMalformedAppRegistration()
    {
        var arm = new AzureArmConfiguration(Guid.NewGuid().ToString("D"), "rg", "account");
        using var http = new HttpClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => AzureArmInventory.ListDeploymentsAsync(
            arm, "not-a-guid", Guid.NewGuid().ToString("D"), null, http, CancellationToken.None));
        Assert.Equal("invalid_azure_app_registration", error.Code);
    }

    [Fact]
    public async Task ArmInventoryIsAHostedFeatureOnlyAndNeverUsesApiKey()
    {
        await Assert.ThrowsAsync<ProviderException>(() => AzureArmInventory.ListDeploymentsAsync(
            new AzureArmConfiguration(Guid.NewGuid().ToString("D"), "rg", "account"),
            "not-a-guid", "not-a-guid", null, new HttpClient(), CancellationToken.None));
    }
}
