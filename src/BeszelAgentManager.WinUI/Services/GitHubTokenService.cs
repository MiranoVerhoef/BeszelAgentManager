using System.Security.Cryptography;
using System.Text;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class GitHubTokenService
{
    private readonly ConfigService _configService = new();

    public async Task<string> GetEffectiveTokenAsync(CancellationToken cancellationToken = default)
    {
        var environmentToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim();
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken;
        }

        var config = await _configService.LoadAsync(cancellationToken);
        return Decrypt(config.GitHubTokenEncrypted);
    }

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadAsync(cancellationToken);
        config.GitHubTokenEncrypted = string.IsNullOrWhiteSpace(token)
            ? string.Empty
            : Encrypt(token.Trim());
        await _configService.SaveAsync(config, cancellationToken);
    }

    public async Task ClearSavedTokenAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadAsync(cancellationToken);
        config.GitHubTokenEncrypted = string.Empty;
        await _configService.SaveAsync(config, cancellationToken);
    }

    private static string Encrypt(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return string.Empty;
        }

        var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(raw);
    }

    private static string Decrypt(string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return string.Empty;
        }

        try
        {
            var raw = Convert.FromBase64String(ciphertext.Trim());
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
