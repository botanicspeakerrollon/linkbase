# CSV import demo: Swiss public transport

Adds 13 Swiss landmarks to the full timetable (3.6M records and 45M links), and links each one to the real stops you reach
it from. It shows that importing into a large model is as quick as into a small one. Switch to Swiss public transport in the
rail and open Import CSV.

1. **`landmarks.csv`**: a new Landmark type.
   - Leave the type as "A new type". The name, Landmark, comes from the file name.
   - Key `id`, name `name`. `elevation_m`, `lat` and `lon` are guessed as numbers and `unesco` as true or false.
   - **Check**, then **Import 13**. Each step takes a second or two.
2. **`landmark-stops.csv`**: links from landmarks to stops.
   - It opens as "Links between records", with Landmark from `landmark` and Stop from `stop_id` chosen already, because the
     headers name the types. Type the relationship, `REACHED_FROM`. The `stop_name` column is only there to make the file
     readable.
   - **Check** says 19 new links, and skips the last row: stop `9999999` isn't in the timetable.
   - **Import 19**, then open the Matterhorn (`/data/Landmark/matterhorn`). It's reached from Zermatt and Gornergrat. Follow
     a stop to see the services calling there.

The stop ids are the timetable's own, such as `8501689` for Zermatt. Landmark positions and heights are approximate.

**Recent imports** undoes them: the links first, then the landmarks, which also removes the Landmark type.
