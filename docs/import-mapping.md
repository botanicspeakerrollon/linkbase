# Import mapping

Status: implemented and verified. The UKC and Mountain Project CSVs load into Postgres. Before the logbook's earlier store
was retired, a row-by-row comparison with it found no differences in ascents or trips.

## Decisions made

- **UKC stars are community stars.** They are removed from the grade and not stored.
- **Partial dates** are stored as the first of the month or year, with a new `ascent.date_precision`
  (`day`, `month`, `year`). All rows in both exports have at least a year.
- **The last Mountain Project location level is the crag.** Everything above it becomes nested areas.
- **UKC `Sent x` is a completed send.**

## Schema changes for the CSVs

- `ascent.date_precision` is new, and `ascent.date` is now required.
- `AscentStyle` gains `send` and `hangdog`, for rows that would otherwise be
  mislabelled as `lead` or `other`.
- `GradeSystem` gains `uiaa`, `australian`, `nordic`, `alpine`, `scrambling`, `via_ferrata` and `ice`.

## UKC logbook (`import.csv`)

| Column | Goes to | Notes |
| --- | --- | --- |
| Country | `area` (country) | England, Wales, Scotland and Northern Ireland stay separate, as UKC has them |
| Crag name | `crag` | Directly under the country; the export has no region. Trimmed |
| Climb name | `route.name` | Trimmed |
| Grade | `route.grade` | Stars removed; otherwise as written, such as `E2 5c`, `f6A`, `6a+ S1` |
| Grade System | `route.grade_system` | See below |
| Grade Type | `route.discipline` | See below |
| Style | `ascent.style`, `ascent.style_raw` | See below; `&beta;` decoded to `β` in the raw text |
| Partner(s) | `ascent.partners` | |
| Notes | `ascent.notes` | Whitespace-only notes become empty; line breaks are kept |
| Date | `ascent.date`, `ascent.date_precision` | `20/Aug/26`, `??/Apr/12`, `???/2014`; two-digit years are 20xx |

**Grade System:** British → `uk_trad`, French → `french`, Font → `font`, USA → `yds`, and the new systems
by name. Summits → `other`. Rows marked V-grades can actually contain Font grades (`f6C`), so those become `font`.

**Grade Type:** Trad, Sport, Alpine → same name; Bouldering → `boulder`; Ice → `winter`;
Scrambling, Via Ferrata, Special → `other`.

**Style** (UKC combines who climbed with how it went, and the original is always kept in `style_raw`):

| UKC style | `style` |
| --- | --- |
| Any style containing `dnf` | `dnf` |
| Lead O/S, AltLd O/S, Sent O/S | `onsight` |
| Lead RP, AltLd RP | `redpoint` |
| Lead β | `flash` |
| Lead, AltLd, Lead rpt, AltLd rpt, Lead G/U | `lead` |
| Lead dog, AltLd dog | `hangdog` |
| Sent, Sent x | `send` |
| 2nd (any) | `second` |
| TR (any) | `top_rope` |
| Solo (any), DWS (any) | `solo` |
| `-` | `other` |

**Identity:** the export has no IDs, so `external_ref` uses normalised text: `crag|name` for routes and
`crag|name|date|style|n` for ascents, where `n` numbers repeats. Identical rows are kept as separate ascents,
since same-day laps are plausible.

## Mountain Project ticks (`ticks.csv`)

| Column | Goes to | Notes |
| --- | --- | --- |
| Location | `area`, `crag` | `USA` is added above the first level (a state, kind `region`); middle levels are kind `area`; the last is the crag |
| Route | `route.name` | |
| Rating | `route.grade`, `route.grade_system` | As written. `5.x` → `yds`, `V…` → `v` |
| Route Type | `route.discipline` | First listed: Boulder → `boulder`, Trad → `trad`, Sport → `sport` |
| Length | `route.length_m` | Feet, converted to metres |
| Pitches | `route.pitches` | Highest seen for the route, since a tick may cover only some pitches |
| Date | `ascent.date` | Precision `day` |
| Style, Lead Style | `ascent.style`, `ascent.style_raw` | See below; raw text is such as `Lead / Onsight` |
| Notes | `ascent.notes` | HTML entities decoded (`&#39;` → `'`) |
| URL | `external_ref` | The route ID in the URL is the `external_id`; the URL is stored too |

Your Stars is always -1 (unrated), so `my_rating` stays empty. Avg Stars and Your Rating aren't stored.
Rating Code could help with grade sort keys in Phase 4.

| Style / Lead Style | `style` |
| --- | --- |
| Lead / Onsight | `onsight` |
| Lead / Flash, Flash | `flash` |
| Lead / Redpoint | `redpoint` |
| Lead / Fell/Hung | `hangdog` |
| Lead (no lead style) | `lead` |
| Send | `send` |
| Attempt | `dnf` |
| Follow | `second` |
| TR | `top_rope` |
| Solo | `solo` |

**Identity:** ascents use `route id|date|n`. A route ticked twice on one date is kept as two ascents.

## Overlap between the exports

The two exports are loaded side by side with no matching between them, as a route's name rarely matches across the two
sites.

## Partners and trips

Neither export has partners as people or trips. They're derived by the rules below, with hand-written corrections.

### Load order

1. Import the UKC and Mountain Project CSVs, as mapped above.
2. Apply the corrections.
3. Derive trips.

### Rules

- **Partners:** split on `,` and ` and `, then trim. Names are case-sensitive.
- **Corrections:** the partner rename, the partners added to Mountain Project ticks (for all ticks, and by area),
  and the multi-country trip merges. These go in `data/corrections.json`, git-ignored like the exports.
- **Trips:** consecutive climbing days in one country.
  - A new trip starts after a gap of more than 3 days in the UK, or more than 7 days abroad.
  - Single days in England aren't trips.
  - Ascents with no day join a trip if exactly one trip in that country overlaps their month.
  - Remaining month-only ascents outside England form a "(dates unknown)" trip per country and month, with
    their links marked inferred.
  - Year-only ascents join no trip.
- **Seasons:** computed in queries from the month and country, with southern-hemisphere countries shifted 6 months.
  Not stored.

These Mountain Project rules replace the versions above where they differ:

- **Bouldering:** a tick is bouldering when Route Type contains Boulder and Style is Send, Flash or Attempt.
  Otherwise the discipline is the first listed type.
- **Grade:** from a Rating with both kinds of grade, such as `5.12a V5`, use the V grade for bouldering and the
  `5.x` grade otherwise. A protection rating such as `R` or `PG13` is kept in `route.grade`.
- **Style:** Pinkpoint counts as `redpoint`, `Lead β` as `flash` and Attempt as `dnf`. Length is in feet.

### Schema additions

- `partner` (id, user_id, name, timestamps, import_batch_id), and `ascent_partner` (ascent_id, partner_id).
  These replace the `ascent.partners` text column. The original text stays in `import_batch.raw_payload`.
- `trip` (id, user_id, key, name, start_date, end_date, sort_date, notes, abroad, home, approximate,
  dates_partly_unknown, manual, timestamps, import_batch_id). `key` keeps a trip's id stable across rebuilds, and
  `sort_date` orders trips with unknown dates, which have no start date.
- `ascent.trip_id` (nullable) and `ascent.trip_inferred`. The trip rules give an ascent at most one trip.
- Partners need no `external_ref`, since their name is unique per user.

There's no table for a trip's countries, since they're derived from its ascents.

### Not stored

- Community stars, from either site, as decided.
- An ascent's year, month, day, season and base style, and a trip's year and month: they can be derived.

## Queries to keep

Insights comes from 19 reports that show how the logbook is used:

- **Trips (5):** abroad, biggest, most-travelled partners, per year, countries revisited.
- **Places (4):** crag loyalty, one-visit crags versus regulars, repeats, places not visited for years.
- **Seasonality (4):** by month and country, days per month by year, by season, discipline mix by year.
- **Partners (2):** by trips with countries, by ascents.
- **Lookups (4):** two-day trips, crag name search, ascents per source, schema view. The last two are replaced by
  the reconciliation report and the graph explorer.

They all run on this schema. Those that group by country need a recursive area lookup, because
Mountain Project crags sit up to 7 levels below the country. A small `crag_country` view handles that.
