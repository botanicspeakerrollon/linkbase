# Starting a model from CSV files: financial compliance and KYC

Eight made-up files for the **New model** page (`/new-model`): a small compliance model of customers, their brokerage accounts,
trades in listed companies, and the alerts a compliance team raises, such as buying ahead of an announcement. All the people,
companies and figures are invented.

Use it on a blank model. Open the tab, **Choose CSV files**, and select all eight at once.

## What the guesses should be

| File | Becomes | Notes |
| --- | --- | --- |
| `people.csv` | Person | Key `person_id`, name `full_name`. `born` is a date, `pep` true or false. |
| `companies.csv` | Company | Key `company_id`, name `name`. `listed` is true or false. `ticker` is empty for the private fund. |
| `accounts.csv` | Account | Key `account_id`, no name column, so the key is the name. `person_id` is a link to Person (`HAS_PERSON`), not a field. |
| `trades.csv` | Trade | Links to Account (`HAS_ACCOUNT`) and Company (`HAS_COMPANY`). `shares` and `price` are numbers, `traded_on` a date. |
| `alerts.csv` | Alert | Links to Trade (`HAS_TRADE`). |
| `insiders.csv` | Links, Person to Company | Two key columns and nothing else, so it is links, not a type. |
| `major_holders.csv` | Links, Person to Company | The same pair again, so it needs a relationship of its own. |
| `associates.csv` | Links, Person to Person | `from_person_id` and `to_person_id` both mean Person. |

The link files get `HAS_COMPANY` and `HAS_PERSON` as a first guess. Rename them before creating, or the two Person to Company
files become one relationship:

- `insiders.csv`: `INSIDER_OF`
- `major_holders.csv`: `MAJOR_HOLDER_OF`
- `associates.csv`: `ASSOCIATE_OF`

**Check** should say nothing is wrong. **Create the model** then adds 10 people, 6 companies, 10 accounts, 18 trades and
6 alerts, and the links:

- 9 `HAS_PERSON`, 18 `HAS_ACCOUNT`, 18 `HAS_COMPANY` and 5 `HAS_TRADE`.
- 5 `INSIDER_OF`, 4 `MAJOR_HOLDER_OF` and 4 `ASSOCIATE_OF`.

## Rows that are skipped on purpose

- `accounts.csv`: `ACC-1010` names person `P-99`, who isn't there. The account is added, without a person link.
- `alerts.csv`: `AL-006` names trade `TR-9999`, which isn't there. The alert is added, without a trade link.
- `trades.csv`: `TR-0018` is in twice. The second row is skipped.

## Things to look for afterwards

- In **Explore**, start from an Alert and follow `HAS_TRADE`, then `HAS_ACCOUNT`, then `HAS_PERSON` to see who traded. Alerts `AL-001`
  and `AL-002` are on two accounts that bought Halvorsen Biotech (`C-01`) in the days before it jumped on 19 March 2026.
  Dana Whitcombe (`P-01`) is an insider there, Rob Teague (`P-02`) is her relative, and Mei-Ling Santos (`P-03`) is his friend.
- Rob Teague's account `ACC-1002` has an `expired` KYC review, which is the kind of thing a KYC check should surface.
- Anders Kowalczyk (`P-04`), a politically exposed person, is an insider at Brightwater Energy. His associate Tomas Eriksen
  (`P-06`) bought through `ACC-1007` before the April announcement, and sold after.
- Type `graph` in Explore to see the seven relationships and their counts.

**Recent imports** undoes each import. Undo the link imports first, then the records, because records can't be taken away while
links still point at them.

The model file doesn't use these files, so `model load` ignores them.
