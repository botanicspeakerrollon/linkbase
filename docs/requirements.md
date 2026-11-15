# Linkbase — Requirements Specification

Status: **Draft**, derived from the current build (revised 2026-11-15). Document owner: n/a (single maintainer).

This is a formal requirements specification in the IEEE/ISO 29148 tradition, scaled down to one maintainer's needs: numbered,
testable requirements with a priority and a status, rather than prose. [reference.md](reference.md) describes what the
product *does*, feature by feature, for a reader; this document says what it is *required* to do, for traceability against
tests and against the plans in [history/](history/). It describes the app as built: each requirement was checked against the
code, and one that no longer matches is reworded or Withdrawn, not left standing. Where this file and the app disagree, the
app is right and this file is a bug.

## Conventions

- **Requirement ids** are `<AREA>-<n>`, stable once assigned. A requirement is never renumbered, only retired (marked
  Withdrawn, never reused).
- **Priority**: **M**ust (the product doesn't make sense without it), **S**hould (expected, deferrable), **C**ould (nice to
  have).
- **Status**: **Met** (built and covered by a test), **Partial** (built with a stated gap), **Planned** (agreed, not built),
  **Deferred** (set aside on purpose — see [1.5](#15-out-of-scope-for-now)), **Withdrawn** (no longer required, and the id is
  never reused).
- "Shall" is a binding requirement. "Should" inside a requirement's text is a recommendation within it, not a lower priority
  (priority is the separate column).

---

## 1. Introduction

### 1.1 Purpose

Linkbase lets someone with no query language and no graph database see how their own data connects — people, projects,
suppliers, a transport network, a climbing logbook — and edit it safely. This specification fixes what "safely" and "lets
someone... see" mean in testable terms, for the one app and one database design described in `CLAUDE.md`.

### 1.2 Scope

One ASP.NET Core / Blazor Interactive Server app, one Postgres instance holding one database per model. In scope: the
generic data model and its pages (Explore, lists, records, import, the model editor, home, timeline, cleanup, history, saved
views, Quick-find, the guide), the climbing domain module (logbook, insights, trips, importers), label-based row and field
security, the operational requirements to run one instance, and the demo (one command, and on Azure). Out of scope for this version: a production sign-in scheme, roles and who-may-label permissions (§1.5), and a
managed offering for customers.

### 1.3 Intended readers

The maintainer, when changing behaviour and deciding what a test should check; anyone reviewing a change against what was
agreed; future-self deciding whether a gap is a bug or a known limit.

### 1.4 Definitions, acronyms

See [reference.md Appendix D](reference.md#appendix-d-glossary) for **model**, **record/node**, **link/edge**,
**relationship**, **pattern**, **hook**, **domain module**. Additionally here: **SRS** = this document. **LLM** = the
model, Anthropic or OpenAI-compatible, that reads text for Read text and Ask.

### 1.5 Out of scope for now

Deliberately not required by this version, and not to be treated as a defect:

- A production sign-in scheme (each deployment adds its own; the demo's passwordless picker is not one), roles, and who may
  label or import (**SEC-1**, **SEC-8**; see [security-design.md](history/security-design.md)).
- Multiple customers behind one running instance (one instance, one owner, one or more models).
- A phone-optimised layout for every page.
- Link values and hierarchies inferred by **New model from CSV files** (they are added afterwards in the model editor, or come
  from a model file).
- Renaming a type, and bulk-editing many records' values at once beyond what a CSV update does.
- Phone-width quick logging of climbs.

### 1.6 References

`CLAUDE.md` (architecture and working rules); [reference.md](reference.md) (feature-by-feature description);
[models.md](models.md), [schema.md](schema.md), [audit-history-plan](history/audit-history-plan.md),
[security-design.md](history/security-design.md), [security-roadmap.md](history/security-roadmap.md),
[operations.md](operations.md), [performance.md](performance.md), [deploy/README.md](../deploy/README.md) and
[deploy/azure/README.md](../deploy/azure/README.md).

---

## 2. Overall description

### 2.1 Product perspective

Self-hosted. The app is the only writer of its databases; nothing else may write `node`, `node_link` or the history tables
directly. The one external dependency that is optional is the LLM provider (Anthropic or an OpenAI-compatible server, for Read text and Ask); the app is fully usable with
it disabled.

### 2.2 User classes

| Class | Wants | Representative needs |
| --- | --- | --- |
| **Owner/user** | Explore, edit and import their own data without learning a query language | FUN-\*, UI-\* |
| **Operator** (may be the same person) | Run, back up and upgrade the instance | OPS-\*, maintains `CLAUDE.md`'s rules |
| **Viewer** (on a model with row security on) | See only what their grants allow, and not be able to tell what is hidden | SEC-\* |
| **Demo visitor** | Try the app on example data with nothing to install, or at a hosted address | DEM-\* |
| **Builder of a new model** | Stand up a model from their own CSVs with no code | MOD-\* |

There is exactly one `AppUser` row per deployment today (**ASM-1**); "user" above means the one person operating it, not a
multi-user audience. A viewer's identity (SEC-\*) decides only what they may see, not whose data it is.

### 2.3 Assumptions and dependencies

- **ASM-1.** Single owner per deployed instance. Every row's `user_id` is `AppUser.DefaultId`; identities under SEC-\* choose
  what is *visible*, not who owns it.
- **ASM-2.** Postgres is reachable and migrated before the app serves traffic other than `/health/*`.
- **ASM-3.** The LLM provider may be unconfigured (no `Anthropic:ApiKey`, or no `Llm:Model` with `Llm:Provider` set to `openai`); features that need it (Read text, Ask) are then unavailable, not broken.
- **ASM-4.** A model's database holds only that model's data; the app never joins across two models' tables.
- **ASM-5.** The person pasting or fetching text for Read text has the right to use it under copyright and any site's
  terms; the app's job is only to avoid storing more than facts (REQ **IMP-8**).

### 2.4 Constraints

- **CON-1.** No lazy loading, no scoped `DbContext` held across a Blazor circuit (`CLAUDE.md`).
- **CON-2.** Foreign keys never cascade.
- **CON-3.** Raw SQL is permitted only for graph/recursive queries, the audit writer and the row-security policies, through keyless types.
- **CON-4.** The container image runs under OpenShift's restricted SCC (arbitrary UID, read-only root FS).
- **CON-5.** Warnings are build errors; MudBlazor v9's analyzer rules apply.

---

## 3. Functional requirements

Each requirement is independently testable. "Record" and "link" are as defined in §1.4.

### 3.1 Data model (DAT)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| DAT-1 | M | The system shall represent domain data as typed records and named, directed relationships between them (a graph), never as free-form rows with no type. | Met |
| DAT-2 | M | Every record shall belong to exactly one model, and no relationship shall be creatable between records of two different models. | Met |
| DAT-3 | M | A record's identity (id) shall be derived from its type and key and shall not change when the model is reloaded, so that saved canvases and external references survive a reload. | Met |
| DAT-4 | M | A field shall be one of: text, number, date, boolean, choice (≤ 50 options), or list of text; a field may be declared `required` and/or `multiple`. | Met |
| DAT-5 | S | A relationship may declare a maximum number of links per source record (`max`), enforced on every write path (form, link dialog, import commit). | Met |
| DAT-6 | S | A relationship may carry its own values ("link attributes"), distinct from either endpoint's fields; a search may filter on them (and Ask may use that), and a CSV re-import may update them. | Met |
| DAT-7 | S | A type's records may be constrained to a hierarchy (levels, parent relationship, which level may sit under which), enforced on create, link, edit and import commit. | Met |
| DAT-8 | C | A type's name may be computed from a template over its own fields, so the record's name need not be entered separately. | Met |
| DAT-9 | C | A type's fields may be put in named groups, shown as headings on the record page, the form and Explore's panel. | Met |

### 3.2 Exploring and searching (FUN)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| FUN-1 | M | The system shall let a user build a multi-step graph search (type, filter, relationship, type, …) without writing a query language, through guided suggestions. | Met |
| FUN-2 | M | The system shall render search results as an interactive node/link diagram and, interchangeably, as a table of the same result or as cards (one per record, showing only its non-empty fields). | Met |
| FUN-3 | M | A node already on the canvas shall not move when further nodes are added by expansion (dragging a node is the one exception: its linked nodes follow it a few hops, like springs). | Met |
| FUN-4 | S | The system shall let a user ask a question in natural language and shall convert it to a graph search pattern, which the user can read and edit before it runs. | Met |
| FUN-5 | M | A natural-language answer (FUN-4) shall be rejected, with no search performed, if it names any type, field, relationship or direction the model does not have, or exceeds 7 steps, 6 conditions on a step or 100 characters in a value. A question is at most 300 characters, and only it and the model's type, field and relationship names shall be sent to the provider. | Met |
| FUN-6 | M | A natural-language answer shall be rejected if any filter value it introduces is not present in the question text. | Met |
| FUN-7 | S | The system shall compute count, sum, minimum, maximum and average for every numeric field across a search's matches, on request. | Met |
| FUN-8 | S | The system shall find the shortest connecting path, in either direction, between two chosen records, up to a bounded number of hops (6 by default), and shall say when it stopped searching through a high-degree node rather than claim there is no path. | Met |
| FUN-9 | S | The system shall list every change made, since a chosen date, to a record, to the records linked to it, and to those links (up to 500 entries), and shall list a selected link's own changes under its values. | Met |
| FUN-10 | S | The system shall let a user view the graph as it existed on a past date, using only data already captured in the audit trail, and shall not claim completeness for a record deleted since or a link that predates audit capture. | Met |
| FUN-11 | S | The current search, canvas contents (up to 150 nodes; beyond that only the search is kept and its first page of matches reopens), expansion filters, graph, table or cards choice and totals state, and a record list's search, sort and filters, shall be encodable in the page's own address (at most 8,000 characters; a longer one is ignored), so it can be reopened or shared by URL. Whatever the address names shall be checked against the model before use. | Met |
| FUN-12 | C | A named "view" (a page's address state, for Explore or a record list) may be saved, reopened, replaced by saving under the same name, deleted, and optionally pinned to the home page. A saved Explore view may also pin the canvas's node positions ("Keep nodes exactly where they are"), so reopening it places the nodes where they were instead of laying them out afresh. | Met |
| FUN-13 | C | ~~A named "scene" (canvas layout only) may be saved and reloaded.~~ | Withdrawn — there is no separate scene; a layout is saved as a view with its positions pinned (FUN-12) |
| FUN-14 | S | The canvas's nodes and links shall be exportable as CSV (nodes), CSV (links), combined JSON, or a PNG image. The nodes CSV shall have a column for every field the model defines for the types shown that the viewer may see, even when no node has a value, so it can be filled in and imported again. | Met |
| FUN-15 | C | The selected node's own page shall open from Explore by keyboard (`o`), in the same tab or, with `Ctrl+O`, a new one so the graph stays as it is. | Met |
| FUN-16 | C | A natural-language question about "the most" of something shall be answerable as a ranking: the records of one type with the most distinct linked records of another, counted in the database over every match of the search, shown with a plain-words description, kept as a visible, editable part of the search phrase, and addable by hand by typing "rank" once the phrase has two steps. A search with no ranking shall look and behave as before. | Met |
| FUN-17 | C | A ranking shall be able to use more than a count of different records: a count of every match with repeats included, or the total, average or highest of a number field of the counted type, listed biggest first or smallest first (among records with at least one match). The choice shall be a visible part of the ranking in the search bar, in the plain-words sentence and in the panel heading, shall be set from a question by Ask, and shall be checked against the model so only a real number field is used. A ranking with no change shall behave as before. Absence ("none") and branching patterns are out of scope. | Met |
| FUN-18 | S | The system shall follow a selected record's links level by level as a tree ("Trace"): downstream, upstream or both ways, optionally for chosen relationships only, 1 to 4 levels (2 by default), 50 links per record with *Show more* for the rest, a count of the links beyond each record (stopping at 100+), a record already above it in the tree left out so loops end, and keyboard navigation of the tree. What the tree shows shall be drawable on the graph (up to 500 records). | Met |
| FUN-19 | S | Expanding a node shall add at most 100 neighbours at a time with *Show more* for the rest, and may be limited to chosen relationships or to a property condition; links may be labelled with their own values; the zoom shall be kept. | Met |
| FUN-20 | S | A model's types may carry a colour, an icon and a caption that Explore, the lists and Quick-find use; typing `graph` shall show the model itself (types as nodes, relationships as links). | Met |

### 3.3 Records and editing (REC)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| REC-1 | M | Every type defined in a model shall automatically get a list page and a record page; no code shall be required to view or edit a generic type's records. | Met |
| REC-2 | M | The record list shall show 50 records a page and support free-text search over the record's name and values, per-field filtering (contains for text and lists, one value for choices and booleans, a range for numbers and dates), and sorting by any shown field, with blank values last. | Met |
| REC-3 | M | Creating or editing a record shall validate every field against its declared type and `required` flag before saving; an invalid save shall be rejected with the reason and nothing partially written. | Met |
| REC-4 | M | A record that still has any link shall not be deletable; the system shall say that links must be removed first. | Met |
| REC-5 | S | Creating a record that requires a link to another type shall allow creating that other record inline, in the same unsaved form, recursively, with the whole tree saved in one transaction or none of it saved. | Met |
| REC-6 | M | A field or type a domain module owns shall not be editable, addable or deletable through the generic forms or CSV import. | Met |
| REC-7 | S | Two or more records may be merged: the kept record retains its own values, gains values it lacked from the others, inherits their links and external references, and the others are deleted, in one transaction with a history entry saying what went where; a merge that would violate DAT-5 or DAT-7 shall be refused with no change made. | Met — a merge cannot be reverted |
| REC-8 | M | A record page shall show every value, every link grouped by relationship in either direction (25 per group, with a count), the record's identifiers in other systems, and its history. | Met |
| REC-9 | S | A new record's key shall be made from its name unless given, and shall not change afterwards. Fields with `multiple` and lists shall be entered one per line. | Met |

### 3.4 Import (IMP)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| IMP-1 | M | The system shall import records and links from a CSV file, guessing type, key, name and link-end columns, with every guess correctable before import. | Met |
| IMP-2 | M | A CSV or text import shall support "check" (report what would happen) separately from committing, with nothing written during check. | Met |
| IMP-3 | M | A CSV or text import shall support staging for review: the user may exclude individual items before committing, and may discard the whole batch with no effect. | Met |
| IMP-4 | M | Every committed import shall be undoable, and undoing shall restore the pre-import state except where doing so would discard a later, independent change — which shall instead be listed to the user, with the import left committed. | Met |
| IMP-5 | S | A CSV import may update, rather than skip, a record whose key already exists, changing only the fields the file actually provides (an empty cell never clears a value), and shall record each changed field in the audit trail; it needs history on. It may likewise update a link's own values, but only where the two records are linked that way exactly once. | Partial — CSV only; Read text still skips existing records and links, and a link file still needs a real key column on both ends |
| IMP-6 | S | The system shall let a user build a new model directly from a set of CSV files with no hand-written model file, inferring types (named from the file), keys, fields and links, with every guess correctable before a no-write check. The model shall get a database of its own, made and migrated on the spot and recorded in the default model's `user_model` table, and the database shall be dropped again if the import fails. Each file's records and then links shall be one undoable import. | Partial — no hierarchies or link values from the files; the model editor (**MOD-4**) covers the rest afterwards |
| IMP-10 | S | A CSV file (comma or semicolon, up to 100 MB) shall be importable as records of one type or links between records already there, with types guessed from the first 1,000 rows; a row with no key column may be identified by name instead. List values split on `\|`. | Met |
| IMP-7 | S | Text pasted or fetched from a web page may be read by an LLM to propose records and links of the current model's types; every proposed name and value shall be verified against the source text before being kept, and anything not verified shall be dropped and reported. | Met |
| IMP-8 | M | An import from text shall store only extracted facts and a brief, paraphrased summary with the source address; it shall never store the source page's full text. | Met |
| IMP-9 | M | Reading text or asking a question (FUN-4, IMP-7) shall be disableable by configuration, and shall be unavailable (not merely hidden) when no model provider is configured. The provider is Anthropic or any OpenAI-compatible server, chosen by configuration. | Met |

### 3.5 History and audit (HIS)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| HIS-1 | M | Every creation, edit, deletion and link change to a record shall be written to an append-only audit trail in the same transaction as the change itself. | Met |
| HIS-2 | M | A manual change shall require a brief reason before it is saved, unless the model has explicitly made the reason optional. | Met |
| HIS-3 | S | A model may disable history entirely; doing so shall not retroactively remove history already recorded. | Met |
| HIS-4 | S | A single manual change (a field edit, a link add/remove/edit, a record creation, or a record deletion) shall be revertible from its entry in the record's history, and the revert shall itself be logged ("Reverted change #n") and shall run the same validation and module hooks as any other change. | Met — reverting a deletion has no button, because a deleted record has no page |
| HIS-5 | M | A revert shall be refused, with no change made, if the state it would restore no longer matches what the audit trail recorded as "before" (something changed again since). | Met |
| HIS-6 | C | A change set spanning more than one record (an import, a correction, a derived rebuild, a merge) shall be revertible as a unit. | Planned — a revert of such a change is refused with the reason; import undo (IMP-4) is the current substitute |
| HIS-8 | S | A save, delete or link shall record a justification of kind manual, import, derived or correction, with a brief note (up to 500 characters) and an optional source (an address, a kind and a comment); a model file may list the source kinds on offer. | Met |
| HIS-7 | S | An import's record creations shall be represented by the batch's own entry rather than one audit row per created record, so that an import of millions of links does not write millions of audit rows. | Met |

### 3.6 Models and extensibility (MOD)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| MOD-1 | M | A new model shall be definable without writing application code, via a declarative model file (types, fields, relationships, sources). | Met |
| MOD-2 | M | Loading a model file shall validate the whole file before writing anything, and shall report every row or record that fails, not only the first. | Met |
| MOD-3 | S | Domain-specific behaviour (derived fields, derived records, custom pages, custom importers) shall be addable by implementing one documented extension point (`IDomainModule`) without modifying generic code. | Met |
| MOD-4 | C | A model's types (with colour and icon), fields (description, group, required, a choice's options; rename; change of type between text, number, date, yes/no and choice; delete, discarding values only on a second confirmation), relationships (add, rename, delete, required, `max`), link values and hierarchy rules shall be editable from within the app once the model exists, without re-loading the model file (which would discard data entered since). A change the existing records or links can't satisfy shall be refused with the reason, and each change shall be one transaction written to the history. | Partial — no bulk edit, and a type can't be renamed |
| MOD-7 | S | Several models shall be usable side by side, each in a database of its own with no links between them: the rail's switcher (hidden when there is one model) shall change the current model, `/model/<key>` shall switch directly, and the choice shall be remembered in a cookie for a year. | Met |
| MOD-8 | M | A model file shall be loadable, and a model's security switched on or off, from the command line (`model load`, `model security`), and migrations shall be applied by a separate `model migrate` command; a load shall replace the model's generic data in one transaction. | Met |
| MOD-5 | M | A field or type a domain module owns (MOD-3) shall not be alterable through MOD-4's editor. | Met |
| MOD-6 | S | A model file may declare security parts (level, any-of, all-of) with their names, the ordered levels, default labels, and where each part's grants come from (the model file, a directory API, sign-in claims, or code). | Met |

### 3.7 Navigation and keyboard (UI)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| UI-1 | M | Every common action shall be reachable from the keyboard alone, without the mouse. | Met |
| UI-2 | S | A single shortcut (Quick-find, `Ctrl K`) shall jump to any record, page, or per-type list from anywhere in the app, including while a text field has focus, and shall start creating a record of any type that is not derived by a module ("Add *Type*"). Records match by name; pages and actions match on every word, those starting with the text first. | Met — adding is the only action |
| UI-6 | S | Keys shall work as follows, and be ignored while typing in a field except where stated: `g` then `h`, `e` or `1`–`9` go to Home, Explore or the nth rail item; `/` focuses the page's search or first field; `?` opens the shortcut list; Explore, Cleanup and the other pages add their own keys (Explore: `t` `s` `v` `a` `f` `c` `n` `p` `e` `w` `r` `d` `o` `Delete` `Esc`; Cleanup: `d` `u` `m` `r`). While a dialog is open only it reacts. | Met |
| UI-3 | M | The in-app help for keyboard shortcuts and the actual key handling shall be driven from one shared list, so they cannot disagree. | Met |
| UI-4 | S | The home page shall show the database's status first (unreachable, or migrations pending and what to run), then the pinned views, a chip per type with its record count, each relationship's link count, and recent activity: records added or edited, changes with their reasons, imports (a staged one linking to its review), and records added per month, without requiring navigation elsewhere. Recent records and changes shall be empty where history is off. | Met |
| UI-5 | C | The home page's `records`, `changes`, `imports` and `growth` panels shall be hideable per model via configuration (`Models:<key>:Home:Hide`). | Met |
| UI-7 | S | The app shall be dark only; colours shall come from the theme, not be hard-coded on a page. | Met |
| UI-8 | S | An in-app guide shall be available for every model (`/guide`) and for the climbing model (`/guide/climbing`); its keyboard table shall come from the same list as the `?` dialog. | Met |

### 3.8 Data housekeeping (CLN)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| CLN-1 | S | The system shall surface likely duplicate records (names matching apart from case/spacing), records with no links, and records missing a required value or link, for a given model, by type (50 groups and 200 items shown), with keys `d`, `u`, `m` and `r` to look again. | Met — fuzzy (misspelt) duplicates are not found |
| CLN-2 | C | Records a domain module derives shall be excluded from duplicate/orphan detection (CLN-1), since the module, not the user, is responsible for them. | Met |

### 3.9 Timeline (TIM)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| TIM-1 | C | The system shall lay out the records of a chosen type by one of its single-valued date fields, newest first (300 at a time), with a bar per month (up to 600 months), a date range filter, and selecting a month narrowing the list. | Met |

### 3.10 Climbing domain (CLM)

The one domain module (`Domains/Climbing`), for models that list it under `Domains`. Its types are Area (country → region → area),
Crag, Route, Ascent, Partner and Trip.

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| CLM-1 | M | The logbook (`/logbook`) shall list every ascent, newest first, 50 a page and sortable, filtered by date range, area, crag, grade system, discipline and style, with a summary of the hardest clean ascent per grade system and counts per discipline and per year (a fall, hangdog, second or top-rope climb is not clean); an ascent shall be addable and editable there. | Met |
| CLM-2 | S | Insights (`/insights`) shall have five tabs: Trips, Places, Seasons, Partners and On this day, each read as the viewer (SEC-2) from the logbook's ascent, trip and partner facts. | Met |
| CLM-3 | M | A route's `grade_rank` shall be derived from its grade; an ascent shall be named after its route; and trips shall be rebuilt when an ascent's date or country changes, logged as one derived change, so trip pages are read-only and a hand edit to a trip is not kept. Multi-country trips shall merge from `data/corrections.json`. | Met |
| CLM-4 | S | The UKC export and the Mountain Project ticks shall import from the command line (`climbing import <model> ukc\|mp\|all`), one undoable batch per file, re-runnable without duplicates, and shall rebuild trips; crags and routes may also be added from text through Read text. | Met — the importers don't check hierarchy rules, and the real model's areas have none |
| CLM-5 | S | The climbing module shall add no tables or views to the database: its pages and Insights queries read the generic store, and the types and fields it derives shall be protected from hand editing (REC-6, MOD-5). | Met |

---

## 4. Non-functional requirements

### 4.1 Performance (PERF)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| PERF-1 | M | Graph search and record lists shall remain usable (sub-second typical queries) on a model of at least 3.5 million records and 45 million links, verified against the Swiss rail sample. | Met — see [performance.md](performance.md) |
| PERF-2 | S | A query that sorts the graph view with a `LIMIT` shall be written so Postgres can find matching rows first, not scan the whole graph. | Met (standing rule, checked per change) |
| PERF-3 | C | Counting links for display (home page, graph-type view) shall use a maintained counter rather than a live count, on models where counting live is too slow. | Met (`link_definition.links`) |
| PERF-4 | S | The app shall be measured with several users at once on models of small, medium and large size, using a repeatable load test that reports throughput and latency percentiles. | Partial — `LoadTests` and `scripts/load-test.ps1` cover reads as the database owner; not covered: row-security viewers, writes, real circuits ([performance.md](performance.md)) |

### 4.2 Security (SEC)

Design: [security-design.md](history/security-design.md). A model opts in by declaring security parts and turning row
security on; a model that doesn't is unaffected (SEC-6).

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| SEC-1 | M | The system shall learn who is using it through one seam (a configured user, the signed-in principal, or the demo picker), and a viewer with no identity shall see nothing of a model that uses levels. | Partial — the seam, `principal` and `demo` exist; no production sign-in scheme ships, each deployment adds its own |
| SEC-2 | M | Row visibility shall be enforced by the database for every read path, including raw SQL graph queries and free-text search, not only EF LINQ, and shall fail closed when the viewer is unset. | Partial — the generic store (`node`, `node_link`, `external_ref`, the graph views), which holds climbing's records too; Insights reads them as the viewer. The import paths and trips still skip the field policy |
| SEC-3 | S | Individual fields (e.g. notes, partner identity, precise location) shall be hideable per viewer, independent of record-level visibility, and shown as a mask rather than left out. | Met — by the field definition's label and per-record overrides (the lock on the record page) |
| SEC-4 | M | Until a production sign-in scheme is in place, the system shall not be exposed beyond a single trusted operator's own machine or network, except behind something that decides who may open it (DEM-4). | Met (documented constraint, not a software control) |
| SEC-5 | M | Visibility shall follow a label of classification (at least), department (any of) and project (all of), on nodes, links and properties, and a link shall be visible only if both its ends are. | Met — nodes, links, field definitions and per-record property overrides |
| SEC-6 | M | A model with security off shall behave and perform as it does without the feature, and turning security off shall leave every record visible. | Met |
| SEC-7 | S | Labels shall be assignable in bulk by selection ("Label (n)…" on a record list), by every record a list matches ("Label all n…"), CSV import and model default, each part offering "Leave as it is" so a bulk change touches only what is set. | Partial — for records; links are labelled only by import or default |
| SEC-8 | S | Only a person permitted to label shall be able to change labels, and import and saved views shall respect the viewer. | Planned — nobody is checked yet |
| SEC-9 | M | History, counts, search hits and graph edges shall not reveal that anything the viewer can't see exists. | Met — history is filtered and masked, link counts are live for restricted viewers |
| SEC-10 | M | Fetching a web page for Read text shall connect only to public addresses: loopback, private, link-local (including a cloud's metadata address), carrier-grade NAT and multicast addresses shall be refused, whatever name or redirect leads to them, and the body shall be limited to 5 MB however it is sent. The reason a fetch failed shall not be shown. | Met — checked on the address connected to; `Extraction:AllowPrivateAddresses=true` lifts it for a deployment whose pages are on its own network |
| SEC-11 | S | Every response shall forbid being framed, sniffed or sending its address on (`X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, and a Content-Security-Policy of `frame-ancestors`, `base-uri`, `object-src` and `form-action` only); the model cookie shall be `Secure` over HTTPS. | Met — no script or style policy, which Blazor and MudBlazor would need relaxed |
| SEC-12 | S | Demo sign-in shall be a POST carrying an anti-forgery token, so a link on another site can't sign a visitor in as someone. | Met — still no password (DEM-4) |
| SEC-13 | S | Anyone who can reach the app can make a model, so no more than 50 may be made in the app, and abandoned CSV uploads shall be removed after a day. | Met |

### 4.3 Reliability and data integrity (REL)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| REL-1 | M | A failed write (validation failure, constraint violation, exception) shall leave neither the data change nor its audit entry — both commit or neither does. | Met |
| REL-2 | M | No foreign key in the schema shall cascade a delete; deleting something still referenced shall fail with a reported error, never silently remove dependents. | Met |
| REL-3 | S | Re-running the same import (CSV or domain importer) with no new data shall change nothing and shall not duplicate records or links. | Met |
| REL-4 | M | Database migrations shall run as an explicit, separate step from application start-up, never implicitly on boot. | Met |

### 4.4 Usability (USE)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| USE-1 | M | A person with no query-language or graph-database background shall be able to find and relate records using only guided search, clicking and plain-English questions. | Met |
| USE-2 | S | The in-app guide shall describe the product in terms of what it does for the user, with no configuration keys, file paths, commands or infrastructure detail. | Met (this version) |
| USE-3 | C | Every page and dialog added to the app shall provide focus handling and at least the baseline keyboard interactions (UI-1). | Met (standing rule, checked per change) |

### 4.5 Operability (OPS)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| OPS-1 | M | The application shall run as a single container image under Docker, Podman, and OpenShift's restricted security context, with no root requirement. | Met |
| OPS-2 | M | The application shall expose separate liveness (`/health/live`) and readiness (`/health/ready`, including "every model's database is migrated") endpoints. | Met |
| OPS-3 | M | Every runtime setting shall be configurable by environment variable; no secret value shall be required to live in a committed file, and every secret shall be listed by name in the operations guide. | Met |
| OPS-4 | S | A documented, scriptable backup and restore procedure shall exist and shall be rehearsable without touching the real database. | Met |
| OPS-5 | S | Logs shall be structured (JSON) and always written to stdout; no reliance on a local log file. | Met |
| OPS-8 | C | Traces, metrics and logs shall be exported in OpenTelemetry's standard form to any collector, only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, with health probes left out of the traces. | Met |
| OPS-6 | C | Off-site backup copies and per-customer monitoring shall exist before the system is operated for anyone other than its single owner. | Planned |
| OPS-7 | S | The system shall ask search engines to stay out (a `robots.txt` that disallows everything, an `X-Robots-Tag: noindex` header, and a meta tag). This is a courtesy, not access control. | Met |

### 4.6 Maintainability (MNT)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| MNT-1 | M | All database access shall go through named repository or service methods; UI components shall never hold or query a `DbContext` directly. | Met (enforced by review / `CLAUDE.md`) |
| MNT-2 | M | A new write path to `node` or `node_link` shall be covered by the audit-trail coverage test, which fails the build if a write path omits logging. | Met |
| MNT-3 | S | Column, table, key and enum naming shall follow one convention (snake_case, enums as snake_case strings) applied centrally, not per migration. | Met |

### 4.7 Demonstration (DEM)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| DEM-1 | S | A person with only a container runtime shall be able to start the app with the four example models (a demo climbing logbook, an example consultancy, a map of this code base and a large ArchiMate model of about 540,000 links) loaded, from nothing, with one command and no .NET SDK, settings file or API key. Starting it again shall not reset what was changed. | Met — `docker-compose.demo.yml`, exercised by a CI job |
| DEM-2 | S | The demo shall include a model with row security on and several example users, so the feature can be shown without a directory. | Met |
| DEM-3 | S | The demo's sign-in shall be passwordless and say so, shall guard everything except the page itself, health probes and static files, and shall never be used with real data. | Met |
| DEM-4 | S | The demo shall be deployable to Azure from one script (Container Apps, Postgres, Key Vault), shall keep the model provider's key out of the repo, the image and the deployment history, and by default shall admit only the deployer's public address; its traces and logs shall go to Application Insights, and one example model's database shall be resettable so a changed model file can be reloaded (`-ResetModel`). | Met — not exercised by CI |
| DEM-5 | C | A hosted demo shall show this specification, so a visitor can read what the app is required to do without a source checkout. | Met — `Docs:Pages` in `appsettings.Demo.json`; off elsewhere |

---

## 5. External interface requirements

| Interface | Direction | Requirement |
| --- | --- | --- |
| Browser (Blazor Server, SignalR circuit) | In/out | The only end-user interface; no separate public API is required by this version. |
| Postgres | Out | One connection string per model; `pg_trgm` extension required for trigram search. |
| LLM provider (Anthropic API, or an OpenAI-compatible server) | Out, optional | Used only for Read text and Ask (IMP-7, FUN-4); absence shall degrade those two features only (ASM-3). |
| Container runtime | In | Environment variables only (OPS-3); port 8080; no mounted config files required. |
| Identity provider | In, optional | Any ASP.NET Core authentication scheme a deployment adds, or the claims it supplies, through the one identity seam (SEC-1). |
| Grant directory | Out, optional | A model's security parts may read people's grants from an HTTP directory with a bearer token (MOD-6). |

### 5.1 Pages

Every routable page, so that a new page cannot be added without being specified here. `RequirementsDocTests` fails when a
`@page` route is missing from this table.

| Route | Page | Requirements |
| --- | --- | --- |
| `/` | Home | UI-4, UI-5 |
| `/explore` | Explore | FUN-1 to FUN-20 |
| `/data/{Type}` | A type's list | REC-1, REC-2 |
| `/data/{Type}/{*Key}` | A record | REC-3 to REC-9, HIS-2, HIS-4 |
| `/logbook` | Logbook (climbing) | CLM-1 |
| `/insights` | Insights (climbing) | CLM-2 |
| `/timeline` | Timeline | TIM-1 |
| `/cleanup` | Cleanup | CLN-1, CLN-2 |
| `/import` | Read text | IMP-7, IMP-9 |
| `/import/review/{BatchId:long}` | Review a staged read | IMP-3, IMP-4, IMP-8 |
| `/import-csv` | CSV import | IMP-1, IMP-2, IMP-5, IMP-10 |
| `/import-csv/review/{BatchId:long}` | Review a staged CSV import | IMP-3, IMP-4 |
| `/model` | Model editor | MOD-4, MOD-5 |
| `/new-model` | New model | IMP-6, MOD-1 |
| `/guide` | Guide | UI-8 |
| `/guide/climbing` | Climbing guide | UI-8 |
| `/docs/{Slug}` | A listed document | DEM-5 |
| `/not-found` | Page not found | — |
| `/Error` | Error page | — |

---

## 6. Appendix A — Requirements → feature traceability

Maps each requirement group to where it is described for a reader and where its tests live.

| Group | Described in `reference.md` | Primarily tested in |
| --- | --- | --- |
| DAT | §1 (concepts), §3 Part III | `Core/Models`, model-loading tests |
| FUN | §4 Explore, §12 Saved views, §13 Ask | `Core/Explore`, `Core/Ask`, `Core/Paths` (path, trace), `Core/Views` tests, `SavedViewTests`, `AskTests` |
| REC | §5 Lists and record pages | `Core/Records` tests |
| IMP | §6, §7, §9 Importing | `Core/CsvImport`, `Core/Extraction`, `Core/Staging` tests |
| HIS | §11 History and revert | `Core/History` tests, `ChangeRecorderCoverageTests` |
| MOD | §2 Models, §7 New model, §8 Model editor, Part III | `ModelLoaderTests`, `ModelEditorTests`, `ModelEditorPageTests`, `ModelCreatorTests`, `ModuleOwnedTests` |
| UI | §14–16 | `Components/Layout` tests, `GuidePageTests` |
| CLN | §10 Home, Timeline and Cleanup | `Core/Cleanup` tests |
| PERF | Part IV | [performance.md](performance.md) |
| SEC | [security-design](history/security-design.md), [models.md](models.md#security) | `RowSecurityTests`, `SecurityRuleTests`, `SecurityLabelsTests`, `FieldPolicyTests`, `HistorySecurityTests`, `IdentityTests`, `LabelUiTests` |
| TIM, CLM | §10 Home, Timeline and Cleanup; §17 climbing | `EverydayUseTests`, `LogbookRepositoryTests`, `InsightsRepositoryTests`, `TripBuilderTests`, `ClimbingHookTests`, `GradeRankingTests` |
| DEM | [deploy/azure/README.md](../deploy/azure/README.md), `docker-compose.demo.yml` | CI `demo` job; `ModelSetupTests` |
| REL, USE, OPS, MNT | Part IV | CI (`.github/workflows/ci.yml`), `.gitlab-ci.yml` |

## 7. Appendix B — Change log

| Date | Change |
| --- | --- |
| 2026-11-14 | First draft, derived from `reference.md` and the plans under `docs/`. |
| 2026-11-15 | SEC-\* no longer Deferred: restated against what is built (row security, field labels, identity seam, demo sign-in), with SEC-8 and SEC-9 added. MOD-4 and MOD-5 built (MOD-4 partial); MOD-6 added. New DEM group, DAT-9, FUN-15, OPS-7; DAT-6, IMP-9 and OPS-3 reworded; §1.5 and §2 updated; security links point at `history/`. |
| 2026-11-15 | DEM-5 met: the demo shows this document in the app (`Docs:Pages`). |
| 2026-11-15 | Security review fixes: SEC-10 to SEC-13 added (public addresses only when fetching a page, response headers and a Secure model cookie, demo sign-in as a POST with a token, a cap on models made in the app and a sweep of old uploads). |
| 2026-11-15 | Full check against the app. FUN-13 (saved scenes) Withdrawn: a layout is now a saved view with pinned positions (FUN-12 reworded). Added FUN-18 (trace), FUN-19, FUN-20, REC-8, REC-9, IMP-10, HIS-8, MOD-7, MOD-8, UI-6 to UI-8, OPS-8, and new groups TIM and CLM (timeline; the climbing domain). Reworded FUN-3, FUN-5, FUN-8, FUN-9, FUN-11, FUN-15, REC-2, REC-7, IMP-5, IMP-6, HIS-4, HIS-6, MOD-4, UI-2, UI-4, UI-5, CLN-1, SEC-7, OPS-5, DEM-1, DEM-4; §1.5's stale claim that the model editor can't edit link values, groups, descriptions or `max` removed; statuses stated where a limit is known. |
| 2026-11-15 | PERF-4 added: a load test simulates several users at once against small, medium and large models (`LoadTests`, `scripts/load-test.ps1`). |
| 2026-11-15 | FUN-2, FUN-11, FUN-14: Explore gains a cards view in the `t` cycle (graph, table, cards), `x` exports the nodes CSV, and that CSV carries every defined field of the types shown, even when empty. |
| 2026-11-15 | PERF-4: `LINKBASE_LOAD_ONLY` runs one step alone; performance.md records the limits found (database CPU, no caching, one replica, connection arithmetic). |
| 2026-11-15 | DEM-4: `deploy.ps1 -ResetAllModels` resets all four example databases at once; `-ResetModel` still takes one or more. |

Update this table whenever a requirement's status or wording changes; don't silently edit history above this line without a
note here.
