# The demo on Azure

Hosts the demo (the example models, with Read text and Ask working) on Azure Container Apps, with your Anthropic key in a
Key Vault. It's one script, and running it again after a change redeploys.

```powershell
winget install Microsoft.AzureCLI      # once; Docker Desktop must be running too
az login
powershell -ExecutionPolicy Bypass -File ./deploy/azure/deploy.ps1   # Bypass is for this one run only; it changes no setting
```

It asks for the Anthropic key at a hidden prompt and ends by printing the address. The first start takes a minute or two,
because it loads the example data.

## What it creates

One resource group (`rg-linkbase` in `newzealandnorth`; `-ResourceGroup` and `-Location` change them):

| Resource | Why | Rough cost a month |
| --- | --- | --- |
| Container App, 0.5 vCPU and 1 GiB (Heavy: 1 vCPU and 2 GiB), one replica | The app. One replica, because Blazor Server keeps each browser's session in its process | about £15 (Heavy £30) |
| Postgres Flexible Server, Burstable B1ms (Heavy: B2ms with 8 GiB and a 128 GiB disk) | The four example models' databases. The large ArchiMate model is sluggish on Light, because it can't be cached | about £12 (Heavy £60) |
| Container Registry, Basic | Holds the image the script builds (with Docker on your PC, then pushes) | about £4 |
| Key Vault | Holds the secrets | pennies |
| Log Analytics | The app's logs | pennies at demo volume |
| Application Insights | The app's OpenTelemetry traces and logs, sent by the environment's managed agent (no setting in the app). Find them under Transaction search or Logs in the portal. Metrics aren't sent, because this agent can't send them to Application Insights | pennies at demo volume |
| Managed identity | How the app reaches the vault and registry, so no password does that job | free |

**Size.** `deploy.ps1` is Light by default, which suits the small models. Add `-Size Heavy` for the large ArchiMate model, and run again to change. Postgres storage can only grow, so Heavy to Light needs the Postgres server recreated.

`./deploy/azure/teardown.ps1` removes everything and stops the charges. It lists what's in the group and asks you to type the
group's name first (`-Yes` skips that, `-NoWait` doesn't wait for Azure to finish, `-ResourceGroup` picks another group).
It is the same as `az group delete --name rg-linkbase`. The vault is then soft-deleted for
seven days, holding its name; the next `deploy.ps1` purges it for you, or `teardown.ps1 -PurgeVault` does it now, which also
erases the secrets for good. Each run also deletes all but the newest three images
from the registry, so repeated deploys leave one app, one database and one vault, not a pile.

## Where the secrets are

| Secret | Held in | Who can read it |
| --- | --- | --- |
| Anthropic key | Key Vault `anthropic-api-key` | The app's managed identity (Key Vault Secrets User), and the account that ran the script (Key Vault Secrets Officer) |
| Postgres password and connection strings | Key Vault `postgres-password`, `cs-*` | The same |

The key is typed into the script's hidden prompt and written to the vault from a temporary file that is deleted straight
away. It is never in the repo, a command line, a Bicep parameter, the deployment history or the image. The Container App
refers to it as a Key Vault secret, so Azure hands it to the app as the `Anthropic__ApiKey` environment variable when the app
starts, and the Azure portal shows the reference, not the value.

Nobody can see it by looking at the app, the portal's resource pages or the repo. Whoever holds a role on the vault can read
it. To make yours write-only once the demo is up, remove your Secrets Officer assignment on the vault (and add it back to
change the key). To change the key later, run `./deploy/azure/deploy.ps1 -ReplaceAnthropicKey`, then
`az containerapp revision restart` (or just run the script, which makes a new revision).

Also set a monthly spend limit for the key in the Anthropic Console, since anyone who can use the demo can spend it.

## Who can reach it

The demo's sign-in page has no password: anyone who reaches the address can be anyone, and can use Read text, which spends
your key. So the script, by default, only lets **your current public address** in (an ingress IP restriction on the Container
App). Pass `-AllowedCidr 203.0.113.0/24,198.51.100.7/32` to let colleagues' addresses in too, and run the script again when your
address changes. `-AllowAnyone` opens it to the internet and warns you.

To show it to people on other networks without listing addresses, put Entra sign-in in front instead: in the portal, open the
Container App, Authentication, Add identity provider, Microsoft, and require authentication. Then use `-AllowAnyone`. That
step creates an app registration in your tenant, so the script doesn't do it for you.

## Search engines

The app asks to be left out of search results: `robots.txt` disallows everything, every response carries an `X-Robots-Tag:
noindex, nofollow, noarchive` header, and pages have a matching meta tag. This only stops crawlers that obey it. It hides
nothing from anyone who has the address, so it is not access control.

The Container App is named `linkbase`, so the address starts `linkbase.` rather than with the random hash the other
resources carry. Changing the name makes the next deploy create a new Container App; delete the old one
(`az containerapp delete --name <old name> --resource-group rg-linkbase`) and the new address replaces the old.

## Notes and limits

- Postgres accepts connections from Azure services and requires a password and TLS. The connection skips checking the server's
  certificate chain (`Trust Server Certificate=true`), which is acceptable for made-up data and would not be for real data.
  Don't load anything real into this deployment.
- `pg_trgm`, which the migrations need, is allowed through the server's `azure.extensions` setting by `main.bicep`.
- Each start of the app runs `model setup` first, in an init container: it creates missing databases and loads example data
  only into an empty model, so restarts don't reset what people changed.
- Setup never loads over a model that already has data, so a changed example file (such as the ArchiMate model growing from
  its small hand-written form to the large generated one) does not reach a deployed demo by itself. Run
  `./deploy/azure/deploy.ps1 -ResetModel archimate` to drop that database and have the new app load the current file (the
  large model takes several minutes). Anything people changed in it is lost.
- To reset the whole demo, delete the databases (or the Postgres server and run the script again).
- `main.bicep` and `deploy.ps1` compile cleanly with `az bicep build` but haven't been run against a real subscription yet: expect to fix a detail or two the first time.
- **Region.** Blazor Server sends every keystroke and click to the server and waits for the reply, so the app feels slow to
  anyone far from it (about 280 ms a round trip between New Zealand and the UK). Pick the region nearest the people using
  it with `-Location`. A region can't be changed in place: delete the group (`az group delete --name rg-linkbase --yes`,
  which removes the data and secrets, so the key is asked for again) and run the script again. Colleagues on other continents will
  still feel the distance; one region is all this script sets up.
