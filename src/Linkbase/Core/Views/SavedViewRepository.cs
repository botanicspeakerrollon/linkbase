using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Views;

public sealed record SavedViewInfo(long Id, string Name, SavedViewKind Kind, string? Scope, string State, bool Pinned)
{
    public string Href => ViewState.Href(Kind, Scope, State);
}

// Saved views: names for states of Explore and of record lists. See ViewState for what the state is.
public sealed class SavedViewRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int MaxNameLength = 80;

    public Task<IReadOnlyList<SavedViewInfo>> ListAsync(CancellationToken cancellationToken = default) => ReadAsync(false, cancellationToken);

    public Task<IReadOnlyList<SavedViewInfo>> PinnedAsync(CancellationToken cancellationToken = default) => ReadAsync(true, cancellationToken);

    private async Task<IReadOnlyList<SavedViewInfo>> ReadAsync(bool pinnedOnly, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SavedViews.AsNoTracking()
            .Where(v => v.UserId == AppUser.DefaultId && (!pinnedOnly || v.Pinned))
            .OrderBy(v => v.Name).ThenBy(v => v.Id)
            .Select(v => new SavedViewInfo(v.Id, v.Name, v.Kind, v.Scope, v.State, v.Pinned))
            .ToListAsync(cancellationToken);
    }

    // Saving under an existing name replaces that view's state, and keeps it pinned if it was.
    public async Task SaveAsync(string name, SavedViewKind kind, string? scope, string state, bool pin, CancellationToken cancellationToken = default)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new LinkbaseValidationException("A view needs a name.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new LinkbaseValidationException($"A view's name can be at most {MaxNameLength} characters.");
        }

        if (string.IsNullOrEmpty(state) || state.Length > ViewState.MaxLength)
        {
            throw new LinkbaseValidationException("That view is too big to save. Narrow the search first.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var view = await db.SavedViews.SingleOrDefaultAsync(v => v.UserId == AppUser.DefaultId && v.Name == trimmed, cancellationToken);
        if (view is null)
        {
            db.SavedViews.Add(new SavedView { UserId = AppUser.DefaultId, Name = trimmed, Kind = kind, Scope = scope, State = state, Pinned = pin });
        }
        else
        {
            view.Kind = kind;
            view.Scope = scope;
            view.State = state;
            view.Pinned |= pin;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.SavedViews
            .Where(v => v.UserId == AppUser.DefaultId && v.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Pinned, pinned).SetProperty(v => v.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.SavedViews.Where(v => v.UserId == AppUser.DefaultId && v.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
