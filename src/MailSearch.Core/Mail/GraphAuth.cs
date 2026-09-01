using MailSearch.Config;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace MailSearch.Mail;

/// <summary>Entra ID sign-in for a public client, with an encrypted on-disk token cache.</summary>
public sealed class GraphAuth
{
    public static readonly string[] Scopes = ["User.Read", "Mail.Read"];

    private readonly GraphConfig _config;
    private readonly DataPaths _paths;
    private readonly Action<string>? _devicePrompt;
    private IPublicClientApplication? _app;

    /// <param name="devicePrompt">Shows the device-code sign-in instructions (URL + code). The status
    /// bar truncates, so the desktop app passes a handler that opens a copyable prompt.</param>
    public GraphAuth(GraphConfig config, DataPaths paths, Action<string>? devicePrompt = null)
    {
        _config = config;
        _paths = paths;
        _devicePrompt = devicePrompt;
    }

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;
        var clientId = string.IsNullOrWhiteSpace(_config.ClientId) ? GraphConfig.DefaultClientId : _config.ClientId;

        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.TenantId)
            .WithDefaultRedirectUri()
            .Build();

        var props = new StorageCreationPropertiesBuilder(_paths.TokenCacheFile, _paths.Root)
            .WithLinuxKeyring("mailsearch", MsalCacheHelper.LinuxKeyRingDefaultCollection, "Graph token cache",
                new KeyValuePair<string, string>("Version", "1"), new KeyValuePair<string, string>("App", "mailsearch"))
            .WithMacKeyChain("mailsearch", "graph-token-cache")
            .Build();
        var helper = await MsalCacheHelper.CreateAsync(props);
        try
        {
            helper.VerifyPersistence();
        }
        catch (MsalCachePersistenceException)
        {
            // No secure store available (e.g. headless Linux); fall back to a plain file.
            props = new StorageCreationPropertiesBuilder(_paths.TokenCacheFile, _paths.Root).WithUnprotectedFile().Build();
            helper = await MsalCacheHelper.CreateAsync(props);
        }
        helper.RegisterCache(app.UserTokenCache);
        return _app = app;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var accounts = await app.GetAccountsAsync();
        try
        {
            var silent = await app.AcquireTokenSilent(Scopes, accounts.FirstOrDefault()).ExecuteAsync(ct);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            AuthenticationResult result;
            if (_config.UseDeviceCode)
            {
                result = await app.AcquireTokenWithDeviceCode(Scopes, dc =>
                {
                    (_devicePrompt ?? StatusLog.Post)(dc.Message);
                    return Task.CompletedTask;
                }).ExecuteAsync(ct);
            }
            else
            {
                result = await app.AcquireTokenInteractive(Scopes)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(ct);
            }
            StatusLog.Post($"Signed in as {result.Account.Username}");
            return result.AccessToken;
        }
    }

    public async Task<string?> GetSignedInUserAsync()
    {
        var app = await GetAppAsync();
        var accounts = await app.GetAccountsAsync();
        return accounts.FirstOrDefault()?.Username;
    }

    public async Task SignOutAsync()
    {
        var app = await GetAppAsync();
        foreach (var account in await app.GetAccountsAsync())
            await app.RemoveAsync(account);
    }
}
