# Linkbase — Requirements Specification

Status: **Draft**, derived from the current build (revised 2026-11-15). Document owner: n/a (single maintainer).

This is a formal requirements specification in the IEEE/ISO 29148 tradition, scaled down to one maintainer's needs: numbered,
testable requirements with a priority and a status, rather than prose. [reference.md](reference.md) describes what the
product *does*, feature by feature, for a reader; this document says what it is *required* to do, for traceability against
tests and against the plans in [history/](history/). Where the two disagree, `reference.md` is probably more current — update
this file to match, don't guess.

## Conventions

- **Requirement ids** are `<AREA>-<n>`, stable once assigned. A requirement is never renumbered, only retired (marked
  Withdrawn, never reused).
- **Priority**: **M**ust (the product doesn't make sense without it), **S**hould (expected, deferrable), **C**ould (nice to
  have).
- **Status**: **Met** (built and covered by a test), **Partial** (built with a stated gap), **Planned** (agreed, not built),
  **Deferred** (set aside on purpose — see [1.5](#15-out-of-scope-for-now)).
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
generic data model and its pages (Explore, lists, records, import, home, timeline, cleanup, history), the climbing domain
module, label-based row and field security, the operational requirements to run one instance, and the demo (one command, and
on Azure). Out of scope for this version: a production sign-in scheme, roles and who-may-label permissions (§1.5), and a
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
  label or import (**SEC-1**, **SEC-8**; see [security-design.md](history/security-design.md)). Per-record property labels, and
  row security for the climbing domain's own tables, are not built either (**SEC-3**, **SEC-2**).
- Multiple customers behind one running instance (one instance, one owner, one or more models).
- A phone-optimised layout for every page.
- Editing link values, descriptions, groups or `max` through **Start from CSVs** or its editor (model-file only, until the
  model editor covers them).

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
| FUN-2 | M | The system shall render search results as an interactive node/link diagram and, interchangeably, as a table of the same result. | Met |
| FUN-3 | M | A node already on the canvas shall not move when further nodes are added by expansion. | Met |
| FUN-4 | S | The system shall let a user ask a question in natural language and shall convert it to a graph search pattern, which the user can read and edit before it runs. | Met |
| FUN-5 | M | A natural-language answer (FUN-4) shall be rejected, with no search performed, if it names any type, field, relationship or direction the model does not have. | Met |
| FUN-6 | M | A natural-language answer shall be rejected if any filter value it introduces is not present in the question text. | Met |
| FUN-7 | S | The system shall compute count, sum, minimum, maximum and average for every numeric field across a search's matches, on request. | Met |
| FUN-8 | S | The system shall find the shortest connecting path, in either direction, between two chosen records, up to a bounded number of hops, and shall say when it stopped searching through a high-degree node rather than claim there is no path. | Met |
| FUN-9 | S | The system shall list every change made, since a chosen date, to a record, to the records linked to it, and to those links. | Met |
| FUN-10 | S | The system shall let a user view the graph as it existed on a past date, using only data already captured in the audit trail, and shall not claim completeness for a record deleted since or a link that predates audit capture. | Met |
| FUN-11 | S | The current search, canvas layout, table/graph choice and totals state shall be encodable in the page's own address, so it can be reopened or shared by URL. | Met |
| FUN-12 | C | A named "view" (address state) may be saved and, optionally, pinned to the home page. | Met |
| FUN-13 | C | A named "scene" (canvas layout only) may be saved and reloaded. | Met |
| FUN-14 | S | The canvas's nodes and links shall be exportable as CSV (nodes), CSV (links), combined JSON, or a PNG image. | Met |
| FUN-15 | C | The selected node's own page shall open from Explore by keyboard, in the same tab or a new one. | Met |

### 3.3 Records and editing (REC)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| REC-1 | M | Every type defined in a model shall automatically get a list page and a record page; no code shall be required to view or edit a generic type's records. | Met |
| REC-2 | M | The record list shall support free-text search over the record's name and values, per-field filtering, and sorting by any shown field. | Met |
| REC-3 | M | Creating or editing a record shall validate every field against its declared type and `required` flag before saving; an invalid save shall be rejected with the reason and nothing partially written. | Met |
| REC-4 | M | A record that still has any link shall not be deletable; the system shall say that links must be removed first. | Met |
| REC-5 | S | Creating a record that requires a link to another type shall allow creating that other record inline, in the same unsaved form, recursively, with the whole tree saved in one transaction or none of it saved. | Met |
| REC-6 | M | A field or type a domain module owns shall not be editable, addable or deletable through the generic forms or CSV import. | Met |
| REC-7 | S | Two or more records may be merged: the kept record retains its own values, gains values it lacked from the others, inherits their links and external references, and the others are deleted; a merge that would violate DAT-5 or DAT-7 shall be refused with no change made. | Met |

### 3.4 Import (IMP)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| IMP-1 | M | The system shall import records and links from a CSV file, guessing type, key, name and link-end columns, with every guess correctable before import. | Met |
| IMP-2 | M | A CSV or text import shall support "check" (report what would happen) separately from committing, with nothing written during check. | Met |
| IMP-3 | M | A CSV or text import shall support staging for review: the user may exclude individual items before committing, and may discard the whole batch with no effect. | Met |
| IMP-4 | M | Every committed import shall be undoable, and undoing shall restore the pre-import state except where doing so would discard a later, independent change — which shall instead be listed to the user, with the import left committed. | Met |
| IMP-5 | S | A CSV import may update, rather than skip, a record whose key already exists, changing only the fields the file actually provides, and shall record each changed field in the audit trail. | Partial — CSV records only; links and Read text still skip existing records |
| IMP-6 | S | The system shall let a user build a new model directly from a set of CSV files with no hand-written model file, inferring types, keys, fields and links. | Partial — no editor for the inferred model afterwards; see **MOD-\*** |
| IMP-7 | S | Text pasted or fetched from a web page may be read by an LLM to propose records and links of the current model's types; every proposed name and value shall be verified against the source text before being kept, and anything not verified shall be dropped and reported. | Met |
| IMP-8 | M | An import from text shall store only extracted facts and a brief, paraphrased summary with the source address; it shall never store the source page's full text. | Met |
| IMP-9 | M | Reading text or asking a question (FUN-4, IMP-7) shall be disableable by configuration, and shall be unavailable (not merely hidden) when no model provider is configured. The provider is Anthropic or any OpenAI-compatible server, chosen by configuration. | Met |

### 3.5 History and audit (HIS)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| HIS-1 | M | Every creation, edit, deletion and link change to a record shall be written to an append-only audit trail in the same transaction as the change itself. | Met |
| HIS-2 | M | A manual change shall require a brief reason before it is saved, unless the model has explicitly made the reason optional. | Met |
| HIS-3 | S | A model may disable history entirely; doing so shall not retroactively remove history already recorded. | Met |
| HIS-4 | S | A single manual change (a field edit, a link add/remove/edit, a record creation, or a record deletion) shall be revertible, and the revert shall itself be logged and shall run the same validation and module hooks as any other change. | Met |
| HIS-5 | M | A revert shall be refused, with no change made, if the state it would restore no longer matches what the audit trail recorded as "before" (something changed again since). | Met |
| HIS-6 | C | A change set spanning more than one record (an import, a correction, a derived rebuild) shall be revertible as a unit. | Planned — only single-record reverts exist; import undo (IMP-4) is the current substitute |
| HIS-7 | S | An import's record creations shall be represented by the batch's own entry rather than one audit row per created record, so that an import of millions of links does not write millions of audit rows. | Met |

### 3.6 Models and extensibility (MOD)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| MOD-1 | M | A new model shall be definable without writing application code, via a declarative model file (types, fields, relationships, sources). | Met |
| MOD-2 | M | Loading a model file shall validate the whole file before writing anything, and shall report every row or record that fails, not only the first. | Met |
| MOD-3 | S | Domain-specific behaviour (derived fields, derived records, custom pages, custom importers) shall be addable by implementing one documented extension point (`IDomainModule`) without modifying generic code. | Met |
| MOD-4 | C | A model's types, fields, relationships, link values and hierarchy rules shall be editable from within the app once the model exists, without re-loading the model file (which would discard data entered since). A change the existing records or links can't satisfy shall be refused with the reason, and each change shall be written to the history. | Partial — no bulk edit, and a type can't be renamed |
| MOD-5 | M | A field or type a domain module owns (MOD-3) shall not be alterable through MOD-4's editor. | Met |
| MOD-6 | S | A model file may declare security parts (level, any-of, all-of) with their names, the ordered levels, default labels, and where each part's grants come from (the model file, a directory API, sign-in claims, or code). | Met |

### 3.7 Navigation and keyboard (UI)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| UI-1 | M | Every common action shall be reachable from the keyboard alone, without the mouse. | Met |
| UI-2 | S | A single shortcut shall jump to, or start creating, any record or page from anywhere in the app, including while a text field has focus. | Met |
| UI-3 | M | The in-app help for keyboard shortcuts and the actual key handling shall be driven from one shared list, so they cannot disagree. | Met |
| UI-4 | S | The home page shall summarise the current model (record counts, relationship counts) and recent activity (records, changes, imports) without requiring navigation elsewhere. | Met |
| UI-5 | C | Any home-page panel shall be hideable per model via configuration. | Met |

### 3.8 Data housekeeping (CLN)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| CLN-1 | S | The system shall surface likely duplicate records (names matching apart from case/spacing), records with no links, and records missing a required value or link, for a given model. | Met |
| CLN-2 | C | Records a domain module derives shall be excluded from duplicate/orphan detection (CLN-1), since the module, not the user, is responsible for them. | Met |

---

## 4. Non-functional requirements

### 4.1 Performance (PERF)

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| PERF-1 | M | Graph search and record lists shall remain usable (sub-second typical queries) on a model of at least 3.5 million records and 45 million links, verified against the Swiss rail sample. | Met — see [performance.md](performance.md) |
| PERF-2 | S | A query that sorts the graph view with a `LIMIT` shall be written so Postgres can find matching rows first, not scan the whole graph. | Met (standing rule, checked per change) |
| PERF-3 | C | Counting links for display (home page, graph-type view) shall use a maintained counter rather than a live count, on models where counting live is too slow. | Met (`link_definition.links`) |

### 4.2 Security (SEC)

Design: [security-design.md](history/security-design.md). A model opts in by declaring security parts and turning row
security on; a model that doesn't is unaffected (SEC-6).

| Id | Priority | Requirement | Status |
| --- | --- | --- | --- |
| SEC-1 | M | The system shall learn who is using it through one seam (a configured user, the signed-in principal, or the demo picker), and a viewer with no identity shall see nothing of a model that uses levels. | Partial — the seam, `principal` and `demo` exist; no production sign-in scheme ships, each deployment adds its own |
| SEC-2 | M | Row visibility shall be enforced by the database for every read path, including raw SQL graph queries and free-text search, not only EF LINQ, and shall fail closed when the viewer is unset. | Partial — the generic store (`node`, `node_link`, `external_ref`, the graph views); not the climbing domain's own tables, so climbing shall not have security turned on |
| SEC-3 | S | Individual fields (e.g. notes, partner identity, precise location) shall be hideable per viewer, independent of record-level visibility, and shown as a mask rather than left out. | Partial — by the field definition's label; per-record overrides are not built |
| SEC-4 | M | Until a production sign-in scheme is in place, the system shall not be exposed beyond a single trusted operator's own machine or network, except behind something that decides who may open it (DEM-4). | Met (documented constraint, not a software control) |
| SEC-5 | M | Visibility shall follow a label of classification (at least), department (any of) and project (all of), on nodes, links and properties, and a link shall be visible only if both its ends are. | Partial — nodes, links and field definitions; not per-record property overrides |
| SEC-6 | M | A model with security off shall behave and perform as it does without the feature, and turning security off shall leave every record visible. | Met |
| SEC-7 | S | Labels shall be assignable in bulk by selection, filter, CSV import and model default. | Partial — for records; links are labelled only by import or default |
| SEC-8 | S | Only a person permitted to label shall be able to change labels, and import and saved views shall respect the viewer. | Planned — nobody is checked yet |
| SEC-9 | M | History, counts, search hits and graph edges shall not reveal that anything the viewer can't see exists. | Met — history is filtered and masked, link counts are live for restricted viewers |

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
| OPS-5 | S | Logs shall be structured (JSON) and written to stdout only; no reliance on a local log file. | Met |
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
| DEM-1 | S | A person with only a container runtime shall be able to start the app with the example models loaded, from nothing, with one command and no .NET SDK, settings file or API key. Starting it again shall not reset what was changed. | Met — `docker-compose.demo.yml`, exercised by a CI job |
| DEM-2 | S | The demo shall include a model with row security on and several example users, so the feature can be shown without a directory. | Met |
| DEM-3 | S | The demo's sign-in shall be passwordless and say so, shall guard everything except the page itself, health probes and static files, and shall never be used with real data. | Met |
| DEM-4 | S | The demo shall be deployable to Azure from one script (Container Apps, Postgres, Key Vault), shall keep the model provider's key out of the repo, the image and the deployment history, and by default shall admit only the deployer's public address. | Met — not exercised by CI |
| DEM-5 | C | A hosted demo shall show this specification, so a visitor can read what the app is required to do without a source checkout. | Planned |

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

---

## 6. Appendix A — Requirements → feature traceability

Maps each requirement group to where it is described for a reader and where its tests live.

| Group | Described in `reference.md` | Primarily tested in |
| --- | --- | --- |
| DAT | §1 (concepts), §3 Part III | `Core/Models`, model-loading tests |
| FUN | §4 Explore | `Core/Explore`, `Core/Ask`, `Core/Paths` tests |
| REC | §5 Lists and record pages | `Core/Records` tests |
| IMP | §6–8 Importing | `Core/CsvImport`, `Core/Extraction`, `Core/Staging` tests |
| HIS | §10 History and revert | `Core/History` tests, `ChangeRecorderCoverageTests` |
| MOD | §2 Models, Part III | model-file loader tests; `Core/Models/ModelEditor*` once released |
| UI | §13–15 | `Components/Layout` tests, `GuidePageTests` |
| CLN | §9 Cleanup | `Core/Cleanup` tests |
| PERF | Part IV | [performance.md](performance.md) |
| SEC | [security-design](history/security-design.md), [models.md](models.md#security) | `RowSecurityTests`, `SecurityRuleTests`, `SecurityLabelsTests`, `FieldPolicyTests`, `HistorySecurityTests`, `IdentityTests`, `LabelUiTests` |
| DEM | [deploy/azure/README.md](../deploy/azure/README.md), `docker-compose.demo.yml` | CI `demo` job; `ModelSetupTests` |
| REL, OPS, MNT | Part IV | CI (`.github/workflows/ci.yml`), `.gitlab-ci.yml` |

## 7. Appendix B — Change log

| Date | Change |
| --- | --- |
| 2026-11-14 | First draft, derived from `reference.md` and the plans under `docs/`. |
| 2026-11-15 | SEC-\* no longer Deferred: restated against what is built (row security, field labels, identity seam, demo sign-in), with SEC-8 and SEC-9 added. MOD-4 and MOD-5 built (MOD-4 partial); MOD-6 added. New DEM group, DAT-9, FUN-15, OPS-7; DAT-6, IMP-9 and OPS-3 reworded; §1.5 and §2 updated; security links point at `history/`. |

Update this table whenever a requirement's status or wording changes; don't silently edit history above this line without a
note here.
