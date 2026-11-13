using Linkbase.Core.Explore;
using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using FieldType = Linkbase.Data.FieldType;

namespace Linkbase.Core.Models;

public sealed partial class ModelEditorPage : ComponentBase
{
    private static readonly string[] Hotkeys = ["t", "r"];

    private ModelOutline? _outline;
    private HistorySettings _history = HistorySettings.Default;
    private int _tab;
    private readonly Dictionary<string, long> _counts = [];

    protected override async Task OnInitializedAsync()
    {
        _history = await Records.HistorySettingsAsync();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _outline = await Editor.OutlineAsync();
        _counts.Clear();
        foreach (var type in _outline.Types.Where(t => t.Owner is null))
        {
            _counts[type.Name] = await Editor.CountRecordsAsync(type.Name);
        }
    }

    private string Count(string type) => (_counts.GetValueOrDefault(type)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Labelled(FieldType type) => type switch
    {
        FieldType.Text => "Text",
        FieldType.Number => "Number",
        FieldType.Date => "Date",
        FieldType.Boolean => "Yes/no",
        FieldType.Choice => "Choice",
        _ => type.ToString(),
    };

    private static string TypeStyle(TypeOutline type) => $"color: {GraphStyle.For(type.Name, type.Color, type.Icon).Color}";

    private Task ChooseTabAsync(int tab)
    {
        _tab = tab;
        return Task.CompletedTask;
    }

    private async Task OnHotkeyAsync(string key)
    {
        switch (key)
        {
            case "t":
                await ChooseTabAsync(0);
                break;
            case "r":
                await ChooseTabAsync(1);
                break;
        }

        await InvokeAsync(StateHasChanged);
    }

    // ---- Types

    private async Task AddTypeAsync()
    {
        var parameters = new DialogParameters<ModelTypeDialog> { { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelTypeDialog>("Add a type", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task EditTypeAsync(TypeOutline type)
    {
        var parameters = new DialogParameters<ModelTypeDialog> { { d => d.Existing, type }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelTypeDialog>($"Edit {type.Name}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task DeleteTypeAsync(TypeOutline type)
    {
        if (await Dialogs.ShowMessageBoxAsync($"Delete {type.Name}?", "Only works while it has no records and isn't in any relationship.", yesText: "Delete", cancelText: "Cancel") != true)
        {
            return;
        }

        try
        {
            await Editor.DeleteTypeAsync(type.Name);
            Snackbar.Add($"Deleted {type.Name}.", Severity.Success);
            await LoadAsync();
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }

    private async Task SetHierarchyAsync(TypeOutline type)
    {
        var parameters = new DialogParameters<HierarchyDialog>
        {
            { d => d.Type, type }, { d => d.Relationships, _outline!.Relationships }, { d => d.Settings, _history },
        };
        var dialog = await Dialogs.ShowAsync<HierarchyDialog>($"{type.Name}'s hierarchy", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task ClearHierarchyAsync(TypeOutline type)
    {
        if (await Dialogs.ShowMessageBoxAsync($"Remove {type.Name}'s hierarchy?", "Records keep their level and parent link; nothing is deleted.", yesText: "Remove", cancelText: "Cancel") != true)
        {
            return;
        }

        try
        {
            await Editor.SetHierarchyAsync(type.Name, null);
            await LoadAsync();
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }

    // ---- Fields and link values

    private async Task AddFieldAsync(FieldHome home)
    {
        var parameters = new DialogParameters<ModelFieldDialog> { { d => d.Home, home }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelFieldDialog>($"Add a {home.Noun} to {home.Label}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task EditFieldAsync(FieldHome home, FieldSpec field)
    {
        var parameters = new DialogParameters<ModelFieldDialog> { { d => d.Home, home }, { d => d.Existing, field }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelFieldDialog>($"Edit {field.Key}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task RenameFieldAsync(FieldHome home, FieldSpec field)
    {
        var parameters = new DialogParameters<RenameFieldDialog> { { d => d.Home, home }, { d => d.OldKey, field.Key }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<RenameFieldDialog>($"Rename {field.Key}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task RetypeFieldAsync(FieldHome home, FieldSpec field)
    {
        var parameters = new DialogParameters<RetypeFieldDialog> { { d => d.Home, home }, { d => d.Field, field }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<RetypeFieldDialog>($"Change {field.Key}'s type", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task DeleteFieldAsync(FieldHome home, FieldSpec field)
    {
        if (await Dialogs.ShowMessageBoxAsync($"Delete {field.Key}?", $"If any {(home.IsLink ? "links" : "records")} have a value for it, you'll be asked again to discard them.", yesText: "Delete", cancelText: "Cancel") != true)
        {
            return;
        }

        try
        {
            await Editor.DeleteFieldAsync(home, field.Key);
            Snackbar.Add($"Deleted {field.Key}.", Severity.Success);
            await LoadAsync();
            return;
        }
        catch (LinkbaseValidationException ex)
        {
            if (await Dialogs.ShowMessageBoxAsync($"Delete {field.Key}?", ex.Message, yesText: "Delete anyway", cancelText: "Cancel") != true)
            {
                return;
            }
        }

        try
        {
            var discarded = await Editor.DeleteFieldAsync(home, field.Key, discardValues: true);
            Snackbar.Add(discarded > 0 ? $"Deleted {field.Key}, discarding it from {discarded:N0}." : $"Deleted {field.Key}.", Severity.Success);
            await LoadAsync();
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }

    // ---- Relationships

    private async Task AddRelationshipAsync()
    {
        var parameters = new DialogParameters<ModelRelationshipDialog> { { d => d.Types, _outline!.Types.Select(t => t.Name).ToList() }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelRelationshipDialog>("Add a relationship", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task EditRelationshipAsync(RelationshipOutline rel)
    {
        var parameters = new DialogParameters<ModelRelationshipDialog> { { d => d.Existing, rel }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<ModelRelationshipDialog>($"Edit {rel.Label}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task RenameRelationshipAsync(RelationshipOutline rel)
    {
        var parameters = new DialogParameters<RenameRelationshipDialog> { { d => d.Existing, rel }, { d => d.Settings, _history } };
        var dialog = await Dialogs.ShowAsync<RenameRelationshipDialog>($"Rename {rel.Label}", parameters);
        if ((await dialog.Result) is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task DeleteRelationshipAsync(RelationshipOutline rel)
    {
        if (await Dialogs.ShowMessageBoxAsync($"Delete {rel.Label}?", "Only works while it has no links.", yesText: "Delete", cancelText: "Cancel") != true)
        {
            return;
        }

        try
        {
            await Editor.DeleteRelationshipAsync(rel.From, rel.Rel, rel.To);
            Snackbar.Add($"Deleted {rel.Label}.", Severity.Success);
            await LoadAsync();
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }
}
