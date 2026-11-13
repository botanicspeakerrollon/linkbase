# CSV import demo: the example consultancy

Five made-up files to show **Import CSV** on the example consultancy model. Switch to it in the rail, open Import CSV, and
import them in this order. Each one shows something different.

1. **`suppliers.csv`**: records of a new type.
   - Leave the type as "A new type". The name, Supplier, comes from the file name.
   - The key column is Supplier ID and the name column is Name, both chosen already. Founded, Staff and Approved are guessed as
     a date, a number and true or false.
   - **Check** says 5 new records, and why two rows are skipped: one has no id, and one repeats `s2`.
2. **`supplies.csv`**: links between records already here.
   - It opens as "Links between records", because the columns `supplier` and `project` name two types. Supplier and Project
     are chosen already. Type the relationship, `SUPPLIES`, as the model has none between them yet.
   - **Check** says 7 new links. It skips one row naming supplier `s9`, which doesn't exist, and one that repeats `s1,pr1`.
   - Open a project afterwards to see its suppliers, or type `graph` in Explore to see the new relationship and its count.
3. **`more-people.csv`**: more records of a type that's already here. It uses semicolons, which are found on their own.
   - Person is chosen already, with the reason underneath: it has Person's fields `title` and `joined`, and `p2` is already
     a Person. The key is `id` and the name is `name`.
   - `team` starts unticked, because it holds Team keys rather than a value. `languages` becomes a new field on Person.
   - **Check** says 3 new records, and that `p2` is already here and left as it is.
   - The same file can go in again as links: Person `id`, Team `team`. Choosing the `team` column chooses Team, and
     `MEMBER_OF` is filled in, as it's the only relationship between the two. That adds 3 links, as `p2` is already in its
     team.
4. **`more-suppliers.csv`**: more Suppliers, but with no id column at all.
   - Supplier is chosen already, and the Key column shows "No key column", because nothing in the file looks like one. Name
     is chosen as the name column, so rows are identified by it instead.
   - Northgate Hosting and Quillfeather Translation match suppliers already here by name, ignoring case. Left unticked,
     **Update records that are already here** leaves them as they are, and Check says 2 new records (the two "Riverside
     Analytics" rows) and 2 already here. Tick it, and Check says 2 new and 2 updated, changing their Staff counts.
   - The two "Riverside Analytics" rows don't match anything here yet, so both are new. The second gets the key
     `riverside-analytics-2`, since the first already took `riverside-analytics`.
5. **`repeat-supplier.csv`**: one more row, also with no key column, naming "Riverside Analytics" again.
   - That name now matches two Suppliers already here, so rather than guess which one, **Check** adds it as new instead,
     with a note explaining why, and gives it the key `riverside-analytics-3`.

**Recent imports** at the bottom undoes each one. Undo them newest first, because records can't be taken away while links
added later still point at them. Undoing the suppliers removes the Supplier type too.

The model file doesn't use these files, so `model load` ignores them.
