# Read API plan

Plans INT-3 in [requirements](../requirements.md#311-integration-int): a read-only, versioned, authenticated HTTP API, so another
program can search a model and read a record without the browser. Events ([change hooks](change-hooks-plan.md)) say *what*
changed; this is how a consumer reads the values, as itself.

## Decisions

- **Bearer tokens.** Callers send `Authorization: Bearer <token>`. That is also how OIDC/JWT access tokens travel, so a client
  never changes when the scheme behind the API does.
- **API keys first.** A key belongs to one user id (the id the model's grant sources look up) and is created with
  `api-key create <user> <name> [days]`. The token is shown once; only a SHA-256 of its secret is kept. Format
  `lbk_<key id>_<secret>`, both parts hex, so a leaked token is recognisable by secret scanners and by the handler (a
  token that doesn't start `lbk_` is left for another scheme, such as JwtBearer).
- **Reads as the caller.** The key's user becomes the current user (`ICurrentUserAccessor`), so row security, field security
  and masking behave as on the pages. There is no "service" identity that sees everything.
- **Swappable in one place.** The API only asks for *an authenticated principal from the scheme named by `Api:Scheme`*
  (default `ApiKey`). To use OIDC: register the JwtBearer scheme in the deployment, set `Api:Scheme` to its name, done.
  `CurrentUser.FromPrincipal` maps any principal to a user (the claim named by `Security:UserClaim`, else
  `preferred_username`, `name`, name identifier or `sub`), and is the same code the pages use, so users are the same people
  whichever way they arrive. Nothing else in the API knows about keys.
- **Keys live in the default model's database** (`api_key`), like the Data Protection keys, because a caller isn't known before
  a model is chosen. The repository opens its own context with no viewer applied, so row security can't be circular.
- **Versioned in the path:** `/api/v1/{model}/...`. An unknown model is 404, and so is a record the caller can't see.
- **Off switch:** `Api:Enabled` (default true).

## Endpoints

| Method and path | Answers |
| --- | --- |
| `GET /api/v1/{model}/search?q=&type=` | Up to 20 matches as Quick-find finds them: `{ results: [{ id, type, key, name, properties }] }`. `type` repeats to narrow. `q` is required. |
| `GET /api/v1/{model}/records/{type}/{key}` | `{ id, type, key, name, values, maskedFields, createdAt, updatedAt, links: [{ rel, direction, total, links: [{ id, type, key, name, values }] }] }`. |

Errors are JSON `{ "error": "..." }`: 400, 401 (with `WWW-Authenticate: Bearer`), 404.

## Keys

- Hash, not encryption: nothing can read a key back. Lookup is by key id, then a fixed-time compare of the hash.
- Revoked or expired keys fail like unknown ones (same 401), so the API doesn't say which.
- `last_used_at` is written at most every 5 minutes per key, so reads don't write on every call.
- Rotation is create a new key, switch the caller, revoke the old one.

## Testing

A small Kestrel host on a random port with the real security services and a database from Testcontainers: wrong, revoked and
expired keys; a valid key reads; row and field security apply to the key's user; unknown model; and a second scheme swapped in
by `Api:Scheme` with no change to the endpoints.

## Not now

Writes (the API is read-only by design), paging a record's links beyond the first few, listing a type's records, and filtering
events by caller. Each is a later step, asked for.
