using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace Lumen.Providers;

public sealed class AzureArmInventory
{
    public const string ArmScope = "https://management.azure.com/.default";

    public static Uri BuildListUri(AzureArmConfiguration arm) => new Uri(
        "https://management.azure.com/subscriptions/" + arm.SubscriptionId
        + "/resourceGroups/" + Uri.EscapeDataString(arm.ResourceGroup)
        + "/providers/Microsoft.CognitiveServices/accounts/" + Uri.EscapeDataString(arm.AccountName)
        + "/deployments?api-version=2025-06-01");

    public static async Task<IReadOnlyList<string>> ListDeploymentsAsync(
        AzureArmConfiguration arm, string tenantId, string clientId,
        Func<DeviceCodeResult, Task>? deviceCode, HttpClient http,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
            throw new ProviderException("invalid_azure_app_registration");
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri("http://localhost")
            .Build();
        AuthenticationResult result;
        try
        {
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            if (accounts.Any())
            {
                try { result = await app.AcquireTokenSilent(new[] { ArmScope }, accounts.First()).ExecuteAsync(cancellationToken).ConfigureAwait(false); }
                catch (MsalException)
                { result = await app.AcquireTokenWithDeviceCode(new[] { ArmScope }, code => deviceCode?.Invoke(code) ?? Task.CompletedTask).ExecuteAsync(cancellationToken).ConfigureAwait(false); }
            }
            else
            {
                result = await app.AcquireTokenWithDeviceCode(new[] { ArmScope }, code => deviceCode?.Invoke(code) ?? Task.CompletedTask).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is MsalException)
        {
            throw new ProviderException("azure_auth_failed");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildListUri(arm));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new ProviderException("network_error"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403) throw new ProviderException("azure_rbac_denied");
                throw new ProviderException(HttpTransport.ErrorCode(response.StatusCode));
            }
            using var document = await HttpTransport.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var value = JsonWire.Get(document.RootElement, "value");
            if (value.ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_response");
            var names = new List<string>();
            foreach (var deployment in value.EnumerateArray())
            {
                var name = deployment.ValueKind == JsonValueKind.Object && deployment.TryGetProperty("name", out var nameElement)
                    ? nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null : null;
                if (name is null || name.Length == 0 || name.Length > 64 || name.IndexOfAny(new[] { '/', '\\', '?', '#', '@', ' ' }) >= 0) continue;
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
            if (names.Count > HttpTransport.MaxCatalogEntries) throw new ProviderException("response_too_large");
            return names;
        }
    }
}
