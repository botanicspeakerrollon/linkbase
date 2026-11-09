using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Linkbase.Data;
using System.Globalization;
using Linkbase.Core.Explore;
using Linkbase.Core.Import;
using Linkbase.Core.Records;
using FieldType = Linkbase.Data.FieldType;

namespace Linkbase.Core.CsvImport;

public partial class CsvImportPanel
{
    private const string NewType = "\u0001new";

    private sealed class FieldChoice
    {
        public bool Include { get; set; } = true;
        public string Key { get; set; } = "";
        public FieldType Type { get; set; }
    }

    private CsvFile? _file;
    private IReadOnlyList<RecordType> _types = [];
    private IReadOnlyList<RelationshipDefinition> _relationships = [];
    private RecentImports? _recentList;
    private Dictionary<int, FieldChoice> _fields = [];
    private bool _reading;
    private bool _importing;
    private bool _links;
    private string _typeChoice = NewType;
    private string? _newType;
    private string? _typeReason;
    private int _keyColumn;
    private int _nameColumn = -1;
    private string? _fromType;
    private string? _toType;
    private int _fromColumn;
    private int _toColumn;
    private string? _relType;
    private CsvImportPlan? _plan;
    private (int Added, string? Type)? _done;

    // Kept so an upload that arrives before the model has loaded waits for it, rather than guessing against nothing.
    private Task _loaded = Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        _loaded = RefreshAsync();
        await _loaded;
    }

    private async Task RefreshAsync()
    {
        // A module's derived records can't be imported, so their types aren't offered.
        _types = (await Records.TypesAsync()).Where(t => t.Owner is null).ToList();
        _relationships = await Records.RelationshipsAsync();
        if (_recentList is not null)
        {
            await _recentList.RefreshAsync();
        }
    }

    private IReadOnlyList<string> Between() =>
        _fromType is null || _toType is null ? [] : CsvImportService.RelationshipsBetween(_relationships, _fromType, _toType);

    // Fills in the relationship when the model has exactly one between the two types, unless one has been typed.
    private void SuggestRelationship()
    {
        var between = Between();
        if (between.Count == 1 && (string.IsNullOrEmpty(_relType) || _relationships.Any(r => r.RelType == _relType)))
        {
            _relType = between[0];
        }
    }

    // The model's relationships between the two types first, then what's typed, then a suggested name and the names the
    // model uses elsewhere, so the list is never empty.
    private Task<IEnumerable<string>> SearchRelationshipsAsync(string? text, CancellationToken cancellationToken)
    {
        var typed = text?.Trim().ToUpperInvariant().Replace(' ', '_') ?? "";
        var suggested = _toType is null ? [] : new[] { $"HAS_{CsvImportService.RelName(_toType)}" };
        var options = Between()
            .Concat(typed.Length > 0 ? [typed] : [])
            .Concat(suggested)
            .Concat(_relationships.Select(r => r.RelType))
            .Where(r => r.Contains(typed, StringComparison.Ordinal))
            .Distinct()
            .ToList();
        return Task.FromResult<IEnumerable<string>>(options);
    }

    private string RelationshipHelp() => Between() switch
    {
        [] => "None between these yet: pick a suggestion or type a name",
        [var only] => $"The model's {only}, or type a new one",
        var several => $"One of {several.Count} the model has, or a new one",
    };

    // Choosing a column whose header names a type chooses that type too.
    private void SetFromColumn(int column) => Changed(() =>
    {
        _fromColumn = column;
        _fromType = CsvImportService.TypeForColumn(Header(column), _types.Select(t => t.Name)) ?? _fromType;
        SuggestRelationship();
    });

    private void SetToColumn(int column) => Changed(() =>
    {
        _toColumn = column;
        _toType = CsvImportService.TypeForColumn(Header(column), _types.Select(t => t.Name)) ?? _toType;
        SuggestRelationship();
    });

    private async Task UploadAsync(IBrowserFile? upload)
    {
        if (upload is null)
        {
            return;
        }

        _reading = true;
        _plan = null;
        _done = null;
        try
        {
            await _loaded;
            if (upload.Size > CsvImportService.MaxBytes)
            {
                throw new LinkbaseValidationException($"{upload.Name} is over {CsvImportService.MaxBytes / 1024 / 1024} MB. Split it into smaller files.");
            }

            await using var stream = upload.OpenReadStream(CsvImportService.MaxBytes);
            var path = await CsvImportService.SaveAsync(stream);
            _file = CsvImportService.Read(path, upload.Name);
            // A column naming another type holds its keys, for linking afterwards, so it isn't a field.
            var typeNames = _types.Select(t => t.Name).ToList();
            _fields = _file.Columns.ToDictionary(c => c.Index, c => new FieldChoice
            {
                Key = CsvImportService.FieldKey(c.Header),
                Type = c.Guess,
                Include = CsvImportService.TypeForColumn(c.Header, typeNames) is null,
            });
            _newType = TypeFromFile(upload.Name);
            _keyColumn = CsvImportService.GuessKeyColumn(_file, _newType);

            // Records of a type that's already here go to it, judged by their fields, keys and the file's name.
            var typeGuess = await Importer.GuessTypeAsync(_file, _keyColumn, _newType, _types);
            _typeChoice = typeGuess?.Type ?? NewType;
            _typeReason = typeGuess?.Reason;
            if (typeGuess is not null)
            {
                ChooseType(typeGuess.Type);
                _keyColumn = CsvImportService.GuessKeyColumn(_file, typeGuess.Type);
            }

            _nameColumn = _file.Columns.FirstOrDefault(c => c.Index != _keyColumn && c.Header.Contains("name", StringComparison.OrdinalIgnoreCase))?.Index ?? -1;

            // Two columns naming types here means links between them.
            var guess = CsvImportService.GuessLinks(_file, _types.Select(t => t.Name).ToList(), _relationships);
            _links = guess is not null;
            _fromType = guess?.FromType;
            _toType = guess?.ToType;
            _fromColumn = guess?.FromColumn ?? 0;
            _toColumn = guess?.ToColumn ?? Math.Min(1, _file.Columns.Count - 1);
            _relType = guess?.RelType;
        }
        catch (LinkbaseValidationException ex)
        {
            _file = null;
            Snackbar.Add(ex.Message, Severity.Warning);
        }
        finally
        {
            _reading = false;
        }
    }

    private string Header(int index) => _file?.Columns.FirstOrDefault(c => c.Index == index)?.Header ?? "";

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    // Any change to the mapping means it needs checking again.
    private void Changed(Action change)
    {
        change();
        _plan = null;
    }

    private void SetKind(bool links) => Changed(() => _links = links);

    private void SetKeyColumn(int column) => Changed(() => _keyColumn = column);

    private void SetNameColumn(int column) => Changed(() => _nameColumn = column);

    // An existing type's fields keep the types it already has, and those a module keeps start unticked.
    private void ChooseType(string choice) => Changed(() =>
    {
        _typeChoice = choice;
        var type = _types.FirstOrDefault(t => t.Name == choice);
        foreach (var field in _fields.Values)
        {
            if (type?.Fields.FirstOrDefault(f => f.Key == field.Key) is { } defined)
            {
                field.Type = defined.Type;
                field.Include &= defined.Owner is null;
            }
        }
    });

    private async Task CheckAsync()
    {
        try
        {
            _plan = _links
                ? await Importer.PlanAsync(_file!, LinkMapping())
                : await Importer.PlanAsync(_file!, RecordMapping());
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }

    private async Task ImportAsync()
    {
        _importing = true;
        try
        {
            var result = _links
                ? await Importer.ImportAsync(_file!, LinkMapping())
                : await Importer.ImportAsync(_file!, RecordMapping());
            _done = (result.Added, _links ? null : RecordMapping().Type);
            _plan = null;
            _file = null;
            await RefreshAsync();
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
        finally
        {
            _importing = false;
        }
    }

    // Stages the file and goes to the review, where it can be committed or discarded.
    private async Task StageAsync()
    {
        _importing = true;
        try
        {
            var batch = _links
                ? await Importer.StageAsync(_file!, LinkMapping())
                : await Importer.StageAsync(_file!, RecordMapping());
            Navigation.NavigateTo($"/import/review/{batch}");
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
        finally
        {
            _importing = false;
        }
    }

    private CsvRecordMapping RecordMapping() => new(
        (_typeChoice == NewType ? _newType : _typeChoice)?.Trim() ?? "",
        _keyColumn,
        _nameColumn < 0 ? null : _nameColumn,
        _fields.Where(f => f.Key != _keyColumn && f.Key != _nameColumn && f.Value.Include)
            .Select(f => new CsvFieldMapping(f.Key, f.Value.Key.Trim(), f.Value.Type))
            .ToList());

    private CsvLinkMapping LinkMapping() =>
        _fromType is null || _toType is null
            ? throw new LinkbaseValidationException("Choose the types at both ends.")
            : new(_fromType, _fromColumn, _relType ?? "", _toType, _toColumn);

    // suppliers.csv suggests Supplier.
    private static string TypeFromFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var words = stem.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        var name = string.Concat(words);
        name = name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y"
            : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1]
            : name;
        return name.Length > 0 && char.IsAsciiLetterUpper(name[0]) ? name : "";
    }
}
