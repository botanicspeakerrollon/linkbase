using System.Globalization;
using Linkbase.Data;

namespace Linkbase.Core.Api;

//   dotnet run --project src/Linkbase -- api-key create <user> <name> [days]   Make a key for a user; shown once, never again
//   dotnet run --project src/Linkbase -- api-key list
//   dotnet run --project src/Linkbase -- api-key revoke <key id>
public sealed class ApiKeyCommands(ApiKeyRepository keys, TimeProvider time, TextWriter output)
{
    public static bool IsCommand(string[] args) =>
        args is ["api-key", "create", _, _] or ["api-key", "create", _, _, _] or ["api-key", "list"] or ["api-key", "revoke", _];

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["api-key", "create", var user, var name, ..]:
                    DateTimeOffset? expires = null;
                    if (args.Length > 4)
                    {
                        if (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
                        {
                            throw new LinkbaseValidationException("Days must be a whole number of 1 or more.");
                        }

                        expires = time.GetUtcNow().AddDays(days);
                    }

                    var (token, info) = await keys.CreateAsync(user, name, expires);
                    await output.WriteLineAsync($"Key {info.KeyId} for {info.UserId}{(expires is null ? ", which does not expire" : $", which expires {expires:yyyy-MM-dd}")}.");
                    await output.WriteLineAsync("Copy it now. It is not kept and can't be shown again:");
                    await output.WriteLineAsync(token);
                    return 0;

                case ["api-key", "list"]:
                    foreach (var key in await keys.ListAsync())
                    {
                        var state = key.RevokedAt is not null ? "revoked" : key.ExpiresAt is { } e && e <= time.GetUtcNow() ? "expired" : "active";
                        await output.WriteLineAsync($"{key.KeyId}  {state,-7}  {key.UserId}  {key.Name}  last used {(key.LastUsedAt is { } used ? used.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never")}");
                    }

                    return 0;

                default:
                    var revoked = await keys.RevokeAsync(args[2]);
                    await output.WriteLineAsync(revoked ? $"Revoked {args[2]}." : $"There is no active key {args[2]}.");
                    return revoked ? 0 : 1;
            }
        }
        catch (LinkbaseValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }
}
