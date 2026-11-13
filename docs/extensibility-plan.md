# What's next

Linkbase aims to become a hosted product for small companies that want to see how their customers, suppliers, projects and
people connect, without buying a graph database or employing someone to model the data. The pitch isn't a cheaper Neo4j: what
these companies lack is the skill to use one. Market claims are unresearched, and any customer's data needs UK GDPR advice first.

## Where it stands

Built: the generic store and model files ([models](models.md)), generated list and record pages, Explore with Totals, CSV
import and AI text reading with review and undo, the audit history, the climbing domain, CI, the container image, and
performance checked on a large sample ([performance](performance.md)). Whether climbing should move into the generic store is
argued in [history/climbing-to-generic-plan.md](history/climbing-to-generic-plan.md).

Settled, and not to reopen without a reason:

- One app instance and one database per customer, so a missed filter can't expose another customer's data.
- The generic store is the product. Climbing is the demo and a personal instance.
- One permission check on every read, including Explore's raw SQL and free-text search. When sign-in is designed, update
  `CLAUDE.md` and [security-roadmap](security-roadmap.md), because "sensitive fields are typed columns" doesn't hold in a
  generic store.

Set aside here: sign-in, roles and field permissions. They must finish before a pilot uses real data. The single-user assumption
(every query uses `AppUser.DefaultId`) goes with them.

## The order

1. ~~**Saved views and permalinks.**~~ Done: Explore and record lists keep their state in the address (`?v=`), a named view is that state saved (`saved_view`), and pinned views show on Home. Not yet: saving a Totals query apart from its search, or a link that opens a view as read-only for someone else, which waits for sign-in.
2. **Quick-find.** Ctrl+K jumps to any record, type or page and runs actions such as adding an ascent. It is the one feature that
   makes the whole app drivable from the keyboard.
3. **Plain-English questions.** *Built: the Ask box on Explore (`Core/Ask`), key A. It sends only the question and the model's type and field names, and `PatternChecker` refuses anything outside the model or not in the question. Saving an answer is the existing saved views.* A box that takes "routes I climbed in Spain above 7a" and compiles it to a search pattern. The
   rules from reading text apply: the model's answer is untrusted, so the result is a pattern checked against the model's types,
   fields and relationships, never SQL. Show the pattern beside the answer so people can learn it and edit it. The large piece
   that sets the product apart, and it needs saved views to exist, so an answer has somewhere to go.
4. **Update on import, and revert.** CSV import and Read text skip existing records, which keeps undo simple. Updating them,
   matched by key with a preview of what would change and the import recorded as the source, is how real data stays current. The
   audit log already holds old values and deleted records, so reverting a change set only needs something to use them. Together
   they let people edit without fear. Run it alongside the hosting work below.
   **Built:** CSV records update on import, undo puts the values back, and each edit on a record page can be reverted
   (see [models](models.md#importing-a-csv-file) and [audit-history-plan](audit-history-plan.md#reverting)). **Left:** updating
   from Read text and links, a way to revert a deleted record, and reverting a change that touched several records.
5. **Hosting.** Off-site backups first, because nothing else matters if the data can be lost, then per-customer setup and
   monitoring. See [operations](operations.md) and [deployment-plan](deployment-plan.md).
6. **A model from a CSV.** A new customer starts from a blank model, not a model file. Built: the Start from CSVs tab infers
   types, fields and relationships from several CSVs ([models](models.md#starting-a-model-from-csv-files)). Still to build: an
   editor for the result, which is what a pilot relies on most, because it works without a developer. Finish values on links in
   the same stretch, through CSV import, Read text and Explore ([relationship-attributes](history/relationship-attributes.md)).
7. **The rest of everyday use.** Built, except phone use. Checked by tests and bUnit pages, not yet by using the running app:
   - **A useful Home.** Recent records, changes and imports, and records added per month, each hideable per model with
     `Models:<key>:Home:Hide` ([models](models.md#configuring-models)). Climbing's Insights page was not folded in.
   - **Cleanup tools.** `/cleanup`: names that match apart from case and spacing, with merge; records with no links; records
     missing a required value or link. Merge is `RecordRepository.MergeAsync`. Fuzzy duplicates (spelling variants) are not found.
   - **Paths and neighbourhoods.** Explore's Find a path (`PathRepository`, at most 6 links by default, says when it gave up on a
     hub), What changed nearby, and History in a link's details.
   - **Time.** `/timeline` for any single-valued date field, and Explore's As of date and slider. As of hides records and links that
     did not exist yet and rewinds values from the audit trail. Records deleted since are not drawn, and links that came in before history was kept look old.
   - **Phone use.** Still to do. Climbing is logged at the crag. Check Logbook and adding an ascent at phone width, make quick logging
     work one-handed, and add a phone-sized browser test.

Items 1 to 3 and 7 came from reading the page list, not from using the app, so check each against the running app before
building it. Every new page or dialog gets focus handling and hotkeys, and keeps to the dark theme and shared styles.
