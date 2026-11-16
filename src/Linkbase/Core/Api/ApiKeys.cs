using System.Security.Cryptography;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Api;

// The shape of a key: lbk_<key id>_<secret>, both hex. Only a SHA-256 of the secret is kept, so a key can't be read back.
public static class ApiKeyToken
{
    public const string Prefix = "lbk_";

    public static (string Token, string KeyId, byte[] SecretHash) Generate()
    {
        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return ($"{Prefix}{keyId}_{secret}", keyId, Hash(secret));
    }

    public static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    // Null for anything that isn't shaped like a key, so a token for another scheme is never looked up.
    public static (string KeyId, string Secret)? Parse(string? token)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token[Prefix.Length..].Split('_');
        return parts is [{ Length: > 0 } id, { Length: > 0 } secret] ? (id, secret) : null;
    }
}

public sealed record ApiKeyInfo(string KeyId, string UserId, string Name, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

// Keys are kept in the default model's database, because a caller isn't known before a model is chosen. This opens its own
// context with no viewer applied, so row security can never be asked who is asking before the answer is known.
public sealed class ApiKeyRepository(ModelCatalog catalog, TimeProvider time)
{
    private static readonly TimeSpan UsedWriteInterval = TimeSpan.FromMinutes(5);

    private LinkbaseDbContext Open() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Default)).Options);

    // The token is returned once and never stored.
    public async Task<(string Token, ApiKeyInfo Info)> CreateAsync(string userId, string name, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(name))
        {
            throw new LinkbaseValidationException("A key needs a user and a name.");
        }

        var (token, keyId, hash) = ApiKeyToken.Generate();
        await using var db = Open();
        var key = new ApiKey { KeyId = keyId, SecretHash = hash, UserId = userId.Trim(), Name = name.Trim(), ExpiresAt = expiresAt, CreatedAt = time.GetUtcNow() };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return (token, ToInfo(key));
    }

    // The user the key stands for, or null for an unknown, wrong, revoked or expired key. Which one isn't said.
    public async Task<string?> ValidateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (ApiKeyToken.Parse(token) is not var (keyId, secret))
        {
            return null;
        }

        await using var db = Open();
        var key = await db.ApiKeys.AsNoTracking().Where(k => k.KeyId == keyId)
            .Select(k => new { k.Id, k.UserId, k.SecretHash, k.ExpiresAt, k.RevokedAt, k.LastUsedAt })
            .SingleOrDefaultAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (key is null || !CryptographicOperations.FixedTimeEquals(key.SecretHash, ApiKeyToken.Hash(secret))
            || key.RevokedAt is not null || (key.ExpiresAt is { } expires && expires <= now))
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > UsedWriteInterval)
        {
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return key.UserId;
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var keys = await db.ApiKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(cancellationToken);
        return keys.Select(ToInfo).ToList();
    }

    public async Task<bool> RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var now = time.GetUtcNow();
        return await db.ApiKeys.Where(k => k.KeyId == keyId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), cancellationToken) > 0;
    }

    private static ApiKeyInfo ToInfo(ApiKey k) => new(k.KeyId, k.UserId, k.Name, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt);
}
