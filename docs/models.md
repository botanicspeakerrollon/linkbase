# Models

A model is one set of data and the types it's made of, such as a climbing logbook, a rail network or a consultancy's people
and projects. Each model has its own database, and the rail's switcher moves between them.

There are two kinds:

- **Generic models** are defined by a model file and loaded with one command. Explore works on them straight away, and their
  home page lists what's in them. This is how a new model starts.
- **Domain models** also have code of their own, plugged in through `IDomainModule` in `Domains/<Domain>/`: pages,
  imports and logic that a model file can't express, such as climbing's grades and trips. Climbing is the only one.

## Trying it

Locally, five models are configured in `appsettings.Development.json`: your climbing logbook, the example consultancy, Swiss
public transport, the demo climbing logbook and an ArchiMate example. The last four live in the `linkbase-samples` container
(see [performance](performance.md)). Hover over the rail and use the switcher by the model's name.

## Setting up the example models

`model setup` makes every model that has a `Seed` ready from nothing: it creates the model's database if the server hasn't
got it, applies the migrations, and loads the seed file if the model has no types or records. It does nothing to a model that
has data, so it is safe to run again, and nothing to a model with no `Seed`, such as your real logbook.

```json
"consultancy": { "Name": "Example consultancy", "ConnectionString": "Consultancy", "Seed": "models/consultancy/model.json" }
```

A `Seed` is a model file, found as given, then beside the app, then from the repo root. The database user needs `CREATEDB`.
Locally, `dotnet run --project src/Linkbase --launch-profile http -- model setup` does the steps under
[the demo climbing logbook](#the-demo-climbing-logbook) and [Adding a model](#adding-a-model) for the sample models in
`appsettings.Development.json`, once the `linkbase-samples` container is running. `docker-compose.demo.yml` runs it in a
container, with `src/Linkbase/appsettings.Demo.json` as the models, and the image carries the example model folders for
it (see the README's Try the demo). Swiss public transport is left out, as its load takes about 100 minutes.

## The demo climbing logbook

`models/climbing-demo/` is a made-up logbook to try the app with, or to show it, without anyone's real data: 45 areas, 39 crags,
442 routes, 1,555 ascents over 176 trips (2016 to 2025) and 20 partners, all invented, in the shape of
`models/climbing/model.json`. Its CSV files are made by `generate.py` with a fixed seed, so running it again gives the same
files. To load it, make a database and load the model file like any other (see [Adding a model](#adding-a-model)):

```powershell
docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE climbing_demo"
dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=climbing_demo;Username=linkbase"
dotnet run --project src/Linkbase --launch-profile http -- model load models/climbing-demo/model.json climbing-demo
```

Its route grade ranks and trips are written into the files, since hooks don't run when a model file is loaded. Reloading
replaces everything, edits included.

## The ArchiMate example

`models/archimate/` is a made-up retail group's enterprise architecture, in ArchiMate's three core layers: 10 business
actors, 8 business roles, 14 business processes, 8 business services and 8 business objects; 12 application components,
10 application services and 10 data objects; 8 technology nodes and 6 technology services, 94 records in all. The 139
links between them use only real ArchiMate relationship kinds (`ASSIGNMENT`, `REALIZATION`, `SERVING`, `ACCESS` and
`TRIGGERING`), reused across layers the way ArchiMate itself reuses them, such as a process realizing a business service
and a component realizing an application service. To load it, make a database and load the model file like any other
(see [Adding a model](#adding-a-model)):

```powershell
docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE archimate"
dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=archimate;Username=linkbase"
dotnet run --project src/Linkbase --launch-profile http -- model load models/archimate/model.json archimate
```

## The model of this code base

`models/linkbase-code/` maps Linkbase itself: folders, source files, C# types (and Razor components) and docs, with who is
defined where, who implements what, which test class tests which type, which files use a type and what the docs mention.
`generate.py` makes the CSV files from `git ls-files`, so run it from inside the repo and reload to bring the model up to date.
Links are found by name, not by compiling, so a type is only linked when its name is unique and at least four letters long.
It is a map to explore, not proof of what depends on what. History is off for it, since it is rebuilt from git. Load it like
any other model (see [Adding a model](#adding-a-model)), with `model load models/linkbase-code/model.json linkbase-code`.

## Climbing in the generic store

Climbing's records are in the generic store like any other model's. Its model file is `models/climbing/model.json`, and the
Climbing module keeps the grade rank and the trips. See [the plan](history/climbing-to-generic-plan.md).

## Adding a model

1. Write a model file, and put its CSV files beside it or in the folder it names. `models/consultancy/` is a small one to copy.
2. Create a database for it and apply the migrations:

   ```powershell
   docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE my_model"
   dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=my_model;Username=linkbase"
   ```

3. Add it to `Models`, with a connection string by the same name (see below).
4. Load it:

   ```powershell
   dotnet run --project src/Linkbase --launch-profile http -- model load models/my-model/model.json my-model
   ```

Loading replaces everything in the model's generic store, in one transaction, so a failed load changes nothing. Node ids are
the type in lower case, a colon and the row's key, such as `stop:8503000`, so they stay the same when a model is reloaded, and
saved views still line up.

## The model file

JSON, with comments allowed. Paths are relative to the model file.

```json
{
  "name": "Example consultancy",
  "folder": "data",
  "types": [
    {
      "name": "Person",
      "color": "#c2410c",
      "icon": "Person",
      "source": { "file": "people.csv", "key": "id", "name": "name" },
      "fields": [ { "key": "title" }, { "key": "joined", "type": "date" }, { "key": "skills", "type": "list" } ]
    }
  ],
  "relationships": [
    {
      "from": "Person", "type": "WORKS_ON", "to": "Project",
      "source": { "file": "assignments.csv", "fromColumn": "person", "toColumn": "project" },
      "fields": [ { "key": "role" }, { "key": "days_a_week", "type": "number" } ]
    }
  ]
}
```

| Setting | Means |
| --- | --- |
| `name` | What the model is called. Shown nowhere yet: the name in `Models` is what the app shows |
| `folder` | Where the CSV files are. The model file's own folder if left out |
| `types[].name` | Letters and digits, starting with a capital, such as `Stop` |
| `types[].color`, `icon` | Optional. A colour such as `#0f766e`, and a [Material icon](https://fonts.google.com/icons)'s name in PascalCase, such as `Train` |
| `types[].source` | The CSV file, the column identifying a row (`key`), and the column to show as its name (`name`, the key if left out). Leave it out for a type that starts empty, such as one a domain module fills |
| `fields[]` | `key` in lower case, digits and underscores. `type` is `text` (the default), `number`, `date`, `boolean`, `choice` or `list`. A choice lists its values in `options` (up to 50), and a value that isn't one of them, spelled exactly, stops the load. A list is several text values in one cell, split by `|`, such as `Figma|User research`. `column` is the CSV column, the key if left out |
| `fields[].description` | Optional, up to 300 characters. Shown beside the field on its form and on the record page, so people know what to put in it |
| `fields[].group` | Optional, up to 40 characters. Fields of a type with the same group are shown together under its heading, such as `Name` over a first and last name. Display only: each stays a separate field in the CSV, filters and history. Not for a relationship's fields |
| `fields[].multiple` | Optional. The field holds several values of its type, such as a list of dates. In a CSV they're split by `\|`, on a form they're one per line (or a multi-select for a choice), and each is checked on its own. A filter matches when any value does. `"type": "list"` is the same as a text field with `multiple`. Not for booleans |
| `types[].nameTemplate` | Optional. Makes each record's name from its fields, such as `"{name_first} {name_last}"`. A field with no value drops out and so does the space around it. People then aren't asked for a name, and it follows the fields when they're edited. Each `{field}` must be a field of the type with one value that people fill in. The source's `name` can hold the template instead, if it has braces |
| `relationships[].max` | Optional. The most links of this kind one record of the `from` type may have, such as `1` for a person's team. With `required: true` that means exactly one. The load stops at a record over it, and the Add link and new record forms refuse a link over it |
| `types[].owner`, `types[].fields[].owner` | Optional. The domain module that writes these records or keeps this value, by its key, such as `Climbing`. See [owned by a module](#owned-by-a-module) |
| `security` | Optional. Which of three kinds of visibility part the model uses, what it calls them, and where people's grants come from. See [security](#security) |
| `history` | Optional. `record` (default `true`) keeps an audit trail of changes, and `requireReason` (default `true`) means a change can't be saved without a reason. See [history](#history) |
| `types[].hierarchy` | Optional. Makes the type's records a tree, with rules about what may be in what. See [hierarchies](#hierarchies) |
| `relationships[].type` | Capitals, digits and underscores, such as `CALLS_AT` |
| `relationships[].source` | The CSV file, and the columns holding the keys of the two ends. They can be the types' own files. Leave it out for a relationship that starts with no links |
| `relationships[].fields` | The values a link of this kind carries, written like a type's `fields`, such as a person's `role` on a project. See [link attributes](#link-attributes). Not `owner` |
| `required` | On a field, `true` means every record needs a value. On a relationship, it means every record of the `from` type needs at least one of its links, such as each ascent's route |

In Explore, a filter on a list matches when any of its values does, so `skills = postgres` finds everyone with Postgres
among their skills.

The example consultancy (`models/consultancy/model.json`) shows `description`, `group`, `multiple`, `nameTemplate` and `max` in use: a
person's name is made from two grouped fields, and a person is in at most one team.

A row without a value for a required field stops the load, and so does a record left without a required relationship's
link, once every link is in. Both say which rows or records.

While loading, a row with an empty key is skipped, a repeated key keeps its first row, and a link whose ends aren't both
loaded is left out. A value that doesn't fit its field's type stops the load and says which field.

### History

Every model keeps an audit trail unless its model file turns it off: what changed, when, and why. Each edit, delete, link
change and import commit saves a reason with it (a short note in your own words, and optionally a web address where it can
be checked), and the record's page shows its history, newest first.

```json
{ "name": "Swiss rail", "history": { "record": false } }
{ "name": "Projects", "history": { "requireReason": false } }
```

- `record: false` keeps no history. It can't be combined with `requireReason: true`, so the load says so.
- `requireReason: false` makes the reason box optional. A reason given is still kept.
- `sourceKinds` lists the kinds of source a change can name, such as `["Internet", "Jira", "SharePoint"]`. A change can carry a source: its kind, an id (a web address or whatever identifies it there) and a brief comment. `Internet` alone if left out.
- The settings are saved with the model each time it is loaded, so a file that leaves `history` out turns both back on.
- Turning history off keeps what was already recorded, and so does a reload.
- An import is one entry for the whole batch, with its source. Its records show "Added by import" at the start of their
  history, and only later edits, deletes and link changes are listed one by one.
- A domain module's rebuild, such as climbing's trips, is one entry of kind Derived listing the records that came, went
  or differed.

See [audit-history-plan](history/audit-history-plan.md) for the design.

### Security

A model can say who may see what. Nothing in the app enforces it yet: this is the part of the
[design](security-design.md) that is built so far, which is the model file's section, the rule, where people's grants
come from, and the labels themselves (stored, loaded from CSV, changeable in bulk). A model with no `security` section
behaves exactly as it always has.

A model chooses which of three **kinds** of part it uses, and names each in its own words. Any one is optional.

| Key | Kind | An item holds | A person holds | Passes when |
| --- | --- | --- | --- | --- |
| `level` | An ordered list, lowest first | one value | one value | the person's value is at least the item's |
| `anyOf` | A set | a set | a set | they share **any one** value, or the item has none |
| `allOf` | A set | a set | a set | the person has **every one** of the item's values |

An item is visible only if every part the model uses passes. Items, links and properties will each carry a label; a link is
visible only if its own label and both its records pass.

```json
"security": {
  "level": { "name": "Sensitivity", "values": [ "public", "internal", "confidential" ] },
  "anyOf": { "name": "Practice", "values": [ "Design", "Engineering", "Delivery" ] },
  "allOf": { "name": "NDA", "values": [ "Harbourline", "Greenleaf", "Copperfield", "Northgate" ] },
  "users": {
    "ava.lindqvist": { "level": "internal", "anyOf": [ "Design" ], "allOf": [ "Harbourline" ] }
  }
}
```

- `name` is what the app will call the part: Classification, Department and Project suit a bank, Sensitivity, Practice and
  NDA suit [the consultancy example](#security-in-the-consultancy-example). A part the model doesn't list doesn't exist for it.
- `values` is required for `level`, which is ordered. For the others it is optional: leave it out to accept any value. A value
  is 1 to 60 characters, matched exactly (including case), with no braces, commas, quotes, backslashes or asterisks and no
  space at either end.
- Each part's grants come from its `source`. The default is `{ "type": "config" }`: the `users` table in the model file,
  for demos and small teams. Or ask a directory the app doesn't own:

  ```json
  "allOf": { "name": "NDA", "source": { "type": "api", "url": "https://directory.example/people/{user}", "path": "ndas", "cacheSeconds": 600, "tokenSetting": "Security:DirectoryToken" } }
  ```

  The app sends a `GET` to `url` with `{user}` replaced by the user's id, reads JSON, and takes the grants from `path` (dots
  go inwards, such as `data.departments`): a string, or for a set an array of strings. `cacheSeconds` is any number of
  seconds (0 keeps nothing; 300 if left out), and parts that share an address share one request. `tokenSetting` names an
  app setting holding a bearer token. The token itself never goes in the model file, and the setting is a secret listed in
  [operations](operations.md#secrets).
- A directory that is down, slow or returns something unexpected grants **nothing** for that part, so people see less,
  never more. An answer is never used after `cacheSeconds`, so a revoked grant stops working within that time. Failures are
  logged and not cached. An unknown level, or a value that isn't valid, is dropped.
- A person with no entry, or no sign-in, holds nothing. In a model that uses a level, that means they see nothing.
- Names of parts are 1 to 40 characters and unique within the model. `https` is required for a directory, except `http` for
  a directory on the same machine. The load stops with every problem listed if any of this is wrong.

#### Labels

Every node and link carries a label: `label_level` (the level's position, 0 being the lowest), `label_any` and `label_all`.
The defaults are the lowest level, no restriction (stored as `'*'`) and no requirement, so existing data is public. A model
that uses security also keeps its `security` section in the database (`model_setting.security`), so the app can validate edits.

A record type says how its records are labelled, and a CSV can override it per row:

```json
"types": {
  "project": {
    "source": { "file": "projects.csv", "key": "id", "name": "name",
                "label": { "level": "sensitivity", "anyOf": "practice", "allOf": "nda" } },
    "label": { "level": "internal" }
  }
}
```

- `source.label` names the CSV columns holding each part. Set columns list values separated by `|`. A blank cell takes the
  type's default; the type's `label` is the default for rows that say nothing (`level`, `anyOf` and `allOf`).
- A relationship has the same two keys, for the labels of its links.
- Loading stops, naming the value, if a label isn't one of the model's values, or if a label is given in a model with no
  `security` section.
- Reloading a model rebuilds its records, so labels come from the model and CSVs, not from hand edits.

`LabelRepository` changes labels in bulk: by node or link ids, setting a level or replacing, adding to or removing from a set.
A batch is one transaction and one history change set (needing a reason where the model asks for one). Items the edit leaves as
they were aren't counted or logged. Label changes can't be reverted from history yet.

#### Security in the consultancy example

[`models/consultancy`](../models/consultancy/model.json) uses all three parts. Its project budgets and each client's work are
commercially sensitive, so each project in `projects.csv` carries a label. Its made-up people hold:

| Person | Sensitivity | Practice | NDA |
| --- | --- | --- | --- |
| `sam.partner` | confidential | Design, Engineering, Delivery | all four clients |
| `ava.lindqvist` | internal | Design | Harbourline |
| `chloe.marsh` | internal | Engineering | Harbourline, Greenleaf |
| `guest` | public | none | none |

| Project | Label | sam | ava | chloe | guest | someone not listed |
| --- | --- | --- | --- | --- | --- | --- |
| pr1 Timetable app | public | yes | yes | yes | yes | no |
| pr2 Booking redesign | internal, Design or Delivery, Harbourline | yes | yes | no | no | no |
| pr3 Stock forecasting | internal (the default), Greenleaf | yes | no | yes | no | no |
| pr4 Patient portal | confidential, Engineering or Delivery, Copperfield | yes | no | no | no | no |
| pr5 Repairs tracker | internal, Northgate | yes | no | no | no | no |

`SecurityLabelsTests` loads these labels and checks who would see what. The person not listed holds nothing, so sees nothing,
even what is public.

### Owned by a module

Some data is worked out by a domain module rather than typed in, such as a route's grade rank or the trips made from a
logbook's ascents. The model file says so with `owner`:

```json
{ "name": "Route", "fields": [ { "key": "grade" }, { "key": "grade_rank", "type": "number", "owner": "Climbing" } ] },
{ "name": "Trip", "owner": "Climbing", "fields": [ { "key": "country" } ] }
```

- **A kept field** is shown on the record page, marked "kept by Climbing", but the edit form has no box for it and the CSV
  import won't take a column for it. Saving keeps its value, and the module's hook sets it.
- **A derived type**'s records are shown like any others, marked as derived, but can't be added, edited, deleted or imported.
  Links to and from them are the module's too: the record page offers none to add or remove, and the CSV import refuses
  them. Deleting a record, or undoing the import that added it, takes its links to derived records with it.

The loader loads owned data from the CSV files like any other, since loading replaces everything anyway. An owner must be
one of the app's domains, and only a type's fields can have one. A kept field that's required needs no column in a CSV
import, since its module fills it in, and a relationship to a derived type can't be required, as nobody could add the
records that need it.

**Hooks.** A module can register an `IRecordHook` in its `AddServices`, naming the types it watches. It runs only for models
that list the module under `Domains`:

- `BeforeSave` runs before a record of a watched type is saved by the edit form or the CSV import, in the same transaction,
  and sets the fields the module keeps.
- `AfterChangeAsync` runs once the changes are saved, with every change one action made to the watched types: records
  created, updated or deleted, with their values before and after, and links added or removed, once for each end. It's
  for keeping derived records up to date.

Climbing's hook (`ClimbingRecordHook`) is the working example: it sets a route's `grade_rank` before it's saved, names each
ascent after its route, and rebuilds the `Trip` records, with `GenericTripService`, when an ascent's date or place changes.

Hooks don't run when a model file is loaded. A module writes its derived records with `RecordRepository.ReplaceDerivedAsync`,
which replaces all of a type's records and their links in one transaction, keeps the relationships' counts, and runs no
hooks.

## Hierarchies

Records of one type can form a tree, with rules about which level may sit in which. The demo climbing logbook's `Area` is the
working example (`models/climbing-demo/model.json`): countries at the top, regions in countries, and areas in regions or in
other areas.

```json
{
  "name": "Area",
  "fields": [ { "key": "kind", "type": "choice", "options": [ "country", "region", "area" ], "required": true } ],
  "hierarchy": {
    "by": "kind",
    "via": "IN",
    "levels": [
      { "kind": "country", "root": true },
      { "kind": "region", "under": [ "country" ] },
      { "kind": "area", "under": [ "region", "area" ] }
    ]
  }
}
```

| Setting | Means |
| --- | --- |
| `by` | The field saying which level a record is on. It must be a required `choice` field of the type |
| `via` | The relationship from a record to its parent. It must be one the model has from the type to itself, and can't be `required`, because the top has no parent |
| `levels[].kind` | One of the `by` field's options. Every option needs a level |
| `root` | A record at this level has no parent. At least one level must be a root |
| `under` | The levels a record at this level may be in. A level that isn't a root needs some, and every level has to be reachable from a root. A level may list itself, which is how areas nest to any depth |

The rules, wherever a record or a link is made:

- A record at a root level has no parent, and any other record is in at most one record of a level its own lists.
- The tree can't go round in a circle, and a record can't be in itself.
- **Loading** a model file checks all of it, including that every record below the top has a parent, and says which records
  break which rule.
- **The new record form** has a Parent box for the type. It's optional there, because the level chosen decides whether one
  is needed. The save is refused if a record below the top has none, or if the parent's level doesn't suit.
- **Add link** checks the same, and says to remove the existing link first if the record is in something already. That is
  also how to move a record: remove the link, then add the new one. Removing a parent is allowed, since a move needs it.
- **Editing a record's level** is refused if it wouldn't suit the record it's in, or the records in it.
- **Importing** (a CSV file, or reading text) checks each link it adds when you commit, and a link that breaks a rule stops
  the whole commit, which changes nothing, and says which records. The batch stays in review so those links can be
  unticked. An import of records alone isn't checked for parents, because it adds no links; add them afterwards.
  Something already wrong in the model doesn't stop an import that doesn't touch it.

Not covered: climbing's UKC and Mountain Project importers write their areas themselves and aren't checked. They build
country, region, then area, which the demo's levels allow. The real climbing model (`models/climbing/model.json`) has no
hierarchy defined yet. Hierarchies are set in the model file only, never by an import.

## Configuring models

```json
"Models": {
  "climbing": { "Name": "Climbing Logbook", "ConnectionString": "Logbook", "Domains": [ "Climbing" ], "Order": 1 },
  "consultancy": { "Name": "Example consultancy", "ConnectionString": "Consultancy", "Order": 2 }
},
"ConnectionStrings": {
  "Consultancy": "Host=127.0.0.1;Port=55433;Database=consultancy;Username=linkbase"
}
```

- **ConnectionString** names an entry under `ConnectionStrings`. A connection string with a password belongs in the git-ignored
  `appsettings.Development.local.json` or an environment variable, never in a committed settings file. See [operations](operations.md#secrets).
- **Domains** lists the domain modules a model uses, by key. Leave it out for a generic model.
- **Order** sets the order in the switcher. The first model is the default.
- **Home:Hide** lists the home page's panels this model leaves out: `records`, `changes`, `imports` or `growth`, as in
  `"Home": { "Hide": [ "growth" ] }`. Growth counts every record, so a model with millions may prefer to hide it. Recent records and
  changes come from the audit trail, so they are empty where history is off.
- **With no `Models` section**, there's one model, using `ConnectionStrings:Linkbase` and every domain. **With one model**, the
  switcher is hidden. That's how a customer's instance should be set up: their model only.

The choice is kept in a cookie for a year, and switching reloads the page. `/model/<key>` switches directly.

## Commands

| Command | Does |
| --- | --- |
| `model list` | Lists the configured models |
| `model migrate` | Applies migrations to every configured model's database, **including your real one** |
| `model load <file> [model]` | Loads a model file into a model, the first one if none is named |
| `model security on\|off [model]` | Turns Postgres row security on or off for a model that has a `security` section (see [security-design](security-design.md)) |
| `climbing import <model> ukc\|mp\|all [--fresh] [--same-database]` | Loads the UKC and Mountain Project exports into `<model>`, one undoable batch per file, and rebuilds the trips. `--fresh` first reloads the model file, which empties the model's records, and needs `--same-database` for the climbing model's own database |

Run them with `dotnet run --project src/Linkbase --launch-profile http -- <command>`. The launch profile matters: without it,
`appsettings.Development.json` and its models aren't read.

## Importing a CSV file

**Import** in the rail opens one page for bringing data in: **CSV file** here, **Read text** (below), and any import a domain
adds. The CSV tab works without writing a model file. Each file is one of two things:

- **Records of one type**, new or already in the model. Choose the column that identifies each row (the key) and the one that
  names it, then which other columns to keep, each as a field with a type. The types are guessed from the first 1,000 rows. A
  new type, and new fields on a type that's already there, join the model. A file with nothing to use as a key — just names,
  no id — can choose **No key column** instead: the name identifies each row, matched against what's already here ignoring
  case, and a new row's key is made from its name the same way a hand-added record's is (`zoe-adams` for "Zoë Adams"). A name
  that matches more than one record already here is added as new rather than guessed, with a note to say so.
- **Links between records already here**, such as which supplier supplies which project: the type and key column at each end,
  and the relationship's name. Import the records first.

A type's required fields each need a column, and a row with nothing in one stops the import. Records of a type that requires
links can't be imported this way, because an import of records adds no links: add them in the app.

It guesses what it can from the headers and the model. A column called `stop`, `stop_id`, `Stop ID`, `stops` or `from_stop_id`
holds Stop keys when there's a Stop type, so a file with two such columns opens as links between them, swapped round if the
model has the relationship the other way. The relationship lists those the model has between the two types, filling in
the only one, and a new name can be typed. For records, it picks the existing type the file looks like, and says why under the
type: keys already there count most, then the file's name, then each column named like one of the type's fields. With no
clear winner it's a new type. A column naming another type starts unticked, as it holds keys rather than a value. The key
is a column called `id`, `key`, `code` or after the type, such as `supplier_id`; with none of those, **No key column** is
chosen instead, and a "name"-looking column is guessed for the name. The first five rows are shown under the
file, to check the choices against.

**Check** says how many rows would be added, and why any are skipped: no key, a key repeated in the file (or, with no key
column, a name repeated, ignoring case), a link to a record that isn't there, or one that's already here. A value that
doesn't fit its field's type stops the import, and the first ten are listed. A list field's values are split by `|`, as in
a model file. A record whose key is already here (or, with no key column, whose name matches, ignoring case) is left as it
is, unless **Update records that are already here** is ticked (see below).

To try it, `models/consultancy/import-demo/` and `models/swiss-rail/import-demo/` have files and a README each, saying what to
choose and what Check should say.

**Import** adds everything at once. **Review first** stages it instead: nothing changes, and the batch appears under
**Recent imports** with a Review link, so a review survives a reload. The review page (`/import/review/<id>`) lists what
would be added, a page at a time, with a box on each item. Untick any to leave them out (or Exclude all, then tick the few
you want), then **Commit** adds the rest, or **Discard** drops the lot. Staging is not CSV-specific: `ImportStagingService`
(`Core/Staging`) takes any importer's records, links and model changes, and holds them in `staged_item` until they're
committed. Committing is one transaction, and skips a record or link that has become a duplicate, or whose ends have gone,
since it was staged. Things already here are never staged, only counted.

**Updating.** With that box ticked, a record whose key is already here takes the file's values where they differ. Only what the
file has changes: an empty cell leaves the value alone, and with no name column the record keeps its name. Check counts the
records it would update, the review lists each one by what changes (`city: Bern → Zurich`), and commit writes one field-level
entry per change to the audit trail, under the import. It needs the model to keep a history, because that is what puts the values
back, and it can't update a type a domain module keeps up to date.

A links file offers the same box, **Update links that are already here**, when the relationship carries values. It only
changes a link's values when the two records are linked that way exactly once: a row that would otherwise match an
ambiguous pair (none yet, or more than one already) is added as a further link instead, same as when the box is unticked.
Read text still only skips what is already here, for both records and links.

Undoing an import also puts back the values it changed. A value someone has changed again since stays as it is and is listed
with the records that stay. A value already back at its old value is skipped, so an undo that left something can be run again.

Undoing an import removes what it added, except a record edited since (its `updated_at` differs from `created_at`), one
something has been linked to since, and one that a record which stays needs through a link its type requires. They're
listed, the import stays committed, and it can be undone again once they're dealt with.

## Starting a model from CSV files

The **Start from CSVs** tab of the Import page builds a model from several files at once, so a new customer needs no model
file. Choose all the files, and it guesses from their names and headers:

- Each file is a type, named from the file (`clients.csv` is `Client`, `people.csv` is `Person`), with a key column
  (`id`, `key`, `code` or the type's id), a name column (the first with "name" in it) and a field for each other column, typed
  from its values.
- A column holding another file's type, such as `client_id` in `projects.csv`, is a link from each project to its client, named
  `HAS_CLIENT`, not a field. A second column to the same type is named after its header.
- A file with columns for two types and no key of its own, such as `assignments.csv` with `person` and `project`, is links
  between them. Its other columns aren't kept, since link values are defined in a model file only.

Everything guessed can be corrected before **Check**, which reports problems in any file and changes nothing. **Create the
model** then imports each file's records, and then the links, through the same service as the CSV tab, so each file is an
import of its own in **Recent imports** and can be undone there. Records are checked before anything is written; a problem
found only in the links is reported once the records are in. Types already in the model are added to, as in the CSV tab.
Its result, like any model's, can then be edited in the app (below).

## Editing a model in the app

**Model** (`/model`) adds, changes and removes a model's types, fields, relationships and link values without a developer or
a model file, through `Core/Models/ModelEditor.cs`. It's checked the same way a model file is: a type's or a relationship's
name follows the same rules, a choice needs 1 to 50 options, and so on. It's also checked against the records and links
already there, which a model file never has to be, because loading one replaces them:

- A choice option still held by a value can't be taken away. A field or relationship can't be made required until every
  record or link already has one. A type can't be deleted while it has records, or is in a relationship; a relationship
  can't be deleted while it has links.
- Changing a field's type (Text to a number, date, yes/no or choice, and back to Text) checks every value fits first, and
  stops with examples when some don't. A choice's options default to the values already there when none are given.
- Renaming a field, or a relationship, keeps its values; only the key moves. A type's name template and hierarchy field
  follow a field it uses when that field is renamed.
- A type or field a domain module derives or keeps can't be changed here. One a module only reads or writes — such as a
  relationship it expects every record to have — can be added to (another field, say) but not renamed, retyped, deleted,
  or have its required or max rules changed.
- Every change is one transaction, and writes the [history](audit-history-plan.md) the same way a hand edit does, under a
  `type:`, `field:`, `relationship:` or `attribute:` entry, so it can be reverted the same way.

A type's hierarchy is set from its required choice field (the level) and a non-required relationship from the type to
itself (the parent): both need to exist first. The levels always cover the field's options exactly, with one at the top and
every other reachable from it, the same rules [a model file's hierarchy](#hierarchies) follows.

**Not built.** Renaming a type: its records' ids embed it (`route:stanage-millstone-edge`), so renaming would mean rewriting
every one and everything that refers to it. Bulk-editing many records' values at once. Groups, descriptions and a
relationship's own limits beyond required and max.

## Reading text

**Read text** finds records and links in pasted text or a fetched page, using the current model. The model's types, fields and
relationships become the extraction tool's schema, so the answer can only name what the model has. Every name and value is
then checked against the source text, and anything not in it, or longer than 200 characters, is dropped and listed in the
review's notes. A type that requires links is only kept when the text gives them. The rest is the same as a CSV import: it is
staged for review, committed in one transaction, and can be undone from **Recent imports**. A record is keyed by the slug of its
name, and one already here, by key or by exact name, is left as it is but can be linked to.

The text is sent to the configured AI model (Anthropic unless `Llm:Provider` says otherwise), and the tab says so. Set `Extraction:Enabled` to `false` to hide it.

Reading text fills text fields and text lists, but leaves out fields that hold several numbers, dates or choices (`multiple`), because it
can't check each value against the text yet.

Each import is one transaction, and **Recent imports** can undo it. Undoing removes the records and links it added,
including changes made to them since, and a type or relationship it added once nothing is left of it. It's refused while
something added since links to one of its records, or if it would leave a record without a link its type requires. Files
can be up to 100 MB, with commas or semicolons. The batch records the file's name and what it added, never its contents.

## Link attributes

A relationship can carry values that belong to a link rather than to either record, such as how many days a week a person
works on a project. The model file lists them under the relationship's `fields`, with the same types, `options` and
`required` as a type's, and the loader reads them from extra columns of the relationship's CSV file (`models/consultancy/assignments.csv`
has `role` and `days_a_week`). They're kept in `node_link.props`, and their definitions in `link_attribute`.

- **Repeats.** The same two records can be linked the same way again only when an attribute's value differs, such as
  Dev Raman being on the portal as an Engineer and again as a Reviewer. A relationship without attributes still allows one
  link between two records. Editing a link into a copy of another is refused the same way.
- **The record page** shows each link's values as chips beside it, an edit button when the relationship has attributes, and
  asks for the values in the Add link dialog. A required attribute can't be left empty there. So can the links a new
  record's form makes, such as a required one to a Project.
- **Explore** draws a link's values on it (`WORKS_ON · Lead · 3`), lists them in the links table, and puts them in the CSV
  and JSON exports. Repeats are separate links on the canvas.
- **The CSV import** of links has a box for each of the relationship's values, which takes the column named like it by
  default. A file can fill the model's values but not define new ones. Rows that repeat a pair with the same values are
  one link, different values are two, and a row with no values counts as already there when the two are linked that way.
- **Read text** is told each relationship's values, and a value the text doesn't contain is dropped like any other. A
  link missing a required value is dropped.
- **Modules** hear `LinkUpdated` when a link's values are edited, with the values before and after. `Linked` carries the
  values it was made with, and `Unlinked` those it had.

## Lists and records

Each type in a model file gets a place in the rail, and pages generated from its definition, with nothing to write:

- **The list** (`/data/<Type>`) shows every record, 50 at a time, with up to six of its fields. Search finds any part of the
  name or a value. Clicking a heading sorts by that field (again for the other way, a third time for name order), with blank
  values last. Filters narrow it by field: text and lists contain the text, choices and booleans are one value, and numbers
  and dates have a from and a to. Values are JSON, so a filter is a lookup in `props`; if one is used often on a big model,
  give it an expression index.
- **The record** (`/data/<Type>/<key>`) shows all its values and its links, grouped by relationship and either way round. A
  group shows its first 25 links and how many there are. Explore's "Open and edit" comes here too.
- **Adding and editing** use a form with a box for each field, checked against its type. A list takes one value per line,
  and a required field can't be left empty. A new record's key is made from its name, such as `zoe-adams`, unless one is
  given, and can't change afterwards, because the record's id is made from it. A new record also chooses the records its
  required links go to, and is saved with them. Each of those can be a new record instead: "New Route" opens the same form
  for it, which can create records of its own in turn, such as a Route's Crag. Nothing is saved until the outer form is, and
  then all of it is, in one transaction, so a failure leaves none of them behind. "Add link" can create the other end too,
  but that one is saved straight away.
- **Links** can be added only where the model has a relationship, and removed one at a time, except a record's last link of
  a kind its type requires. A record that still has links can't be deleted, like anything else in the app. Its own required
  links don't count: they go with it.
- **Ids elsewhere**, such as a UKC id, are kept in `external_ref` by node id, so an import can find the record again. A
  source's id belongs to one record of a type. The record page shows them, linked when they have a web address, and they go
  when the record, its import or the model is replaced.

The home page lists how many records there are of each type and how many links of each kind, and so does the graph
model in Explore (type `graph`), on its types and links. Counting tens of millions of links takes too long to do each time,
so each relationship's count is kept in `link_definition.links`, by the loader and by adding and removing links in the app.
Anything else that writes to `node_link` must keep it up to date too.

**Loading a model file again replaces everything, including what was added or changed in the app.** Once people edit a
model's data in the app, the app is where it lives, so don't load into that model again.

## Limits
- Every model's database has climbing's tables too, empty, because there's one schema for all of them.
- Values are stored as JSON, so the rule about keeping sensitive fields in typed columns doesn't hold for generic models yet.
  See [security](security-roadmap.md).
- Climbing's rule that an ascent's name is its route's applies to any type called `Ascent`.
- Link attributes are defined in the model file, not by an import or by reading text. See
  [relationship attributes](history/relationship-attributes.md).
- A name longer than about 2,000 characters can't be stored, because names are indexed for the list's order.
