# CSV import demo: the example consultancy

Three made-up files to show **Import CSV** on the example consultancy model. Switch to it in the rail, open Import CSV, and
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

**Recent imports** at the bottom undoes each one. Undo them newest first, because records can't be taken away while links
added later still point at them. Undoing the suppliers removes the Supplier type too.

The model file doesn't use these files, so `model load` ignores them.
