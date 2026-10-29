# Academia KB — frontend

Front end for the Knowledge Base & Academic DMS (KBMS), built from the pmtool design
(workspace `usa-1`, project KBMS). Every screen reads and writes through the backend API
(`backend/src/KnowledgeBase.Api`).

Scaffolded with the official `npm create vite@latest` → React → TypeScript, default Oxlint linter.

## Run

Start the API first (see `backend/README.md`; in Development it migrates and seeds the database), then:

```sh
npm install
npm run dev        # http://localhost:5173
npm run build      # type-check + production build into dist/
npm run lint       # oxlint
npm run api:types  # regenerate src/api/schema.d.ts from the running API's OpenAPI document
```

Or run the whole stack in Docker from the repository root with `docker compose up -d --build` (see
`../compose.yaml`) and open http://localhost:25480. The image builds the app with an empty
`VITE_API_BASE_URL`, so it calls `/api` on its own origin, and nginx (`nginx.conf`) proxies that to the API.

## Environment

Settings live in `.env` (committed, no secrets) and are read through `src/app/config.ts`:

| Variable | Default | Meaning |
|---|---|---|
| `VITE_API_BASE_URL` | `http://localhost:5259` | The backend. It must allow this app's origin in `Cors:Origins` (`http://localhost:5173` in Development) |

To override on your machine, create `.env.local` (git-ignored by `*.local`). Vite reads the files
when the dev server starts, so restart `npm run dev` after changing them. Only `VITE_*` variables
reach the browser, and they are baked into the build.

## Signing in, with several accounts

There is no identity provider yet, so sign-in uses the API's development endpoints
(`GET /api/dev/users`, `POST /api/dev/token`): pick one of the active people (a fresh database has only
`admin`, the System Administrator). The API only offers them in Development; with `Auth:Authority`
set, the sign-in page says single sign-on isn't connected yet.

Several people can be signed in at once, like a browser's account switcher:

- The user card at the bottom of the sidebar lists the signed-in accounts. **Switch account**,
  **Add another account**, **Sign out** of one, or **Sign out of all accounts**.
- **Each tab works as one account.** Accounts are shared by every tab (`localStorage`); the account a
  tab uses is kept per tab (`sessionStorage`). So you can submit as faculty in one tab and review as
  the Academic Supervisor in another. A new tab starts with the account used last.
- Signing in or out in one tab updates the others.
- When the API refuses an account's token (expired after `Auth:DevTokenHours`, or the user was
  deactivated), that account shows as signed out and the app asks to sign it in again. Other accounts
  keep working.
- Switching accounts reloads the screen, so nothing from the previous person stays on it.

The sidebar and home page follow what the account may do (`persona` and `capabilities` from
`GET /api/me`): Faculty, Academic Supervisor, College Dean, System Administrator, and the CICT
Director and staff (a non-academic department).

For dev links, add `?as=<account id>` to any URL: `admin` or an id the admin gave someone under
Settings › Users. For example, `/settings?as=admin` opens Settings as the System Administrator, signing
them in first if needed (development sign-in only).

## Talking to the API

```
src/api/
  schema.d.ts   generated from the API's OpenAPI document (npm run api:types) — don't edit
  types.ts      readable aliases: DocumentDetail, SubmissionDetail, MeDto, …
  client.ts     fetch wrapper: bearer token, ProblemDetails → ApiError, If-Match, Idempotency-Key
  useApi.ts     useApi (load data, reload on deps) and useAction (a button's pending/error state)
  cache.ts      per-account cache for reference lists
  org.ts, reference.ts, library.ts, uploads.ts, access.ts, requirements.ts, workflow.ts,
  settings.ts, admin.ts, knowledge.ts, auth.ts   one module per API area
```

- Errors are `ApiError` with the API's `status`, `code`, `title`, `detail` and field `errors`;
  `components/AsyncState` shows them (`Loading`, `ErrorState`, `InlineError`).
- Edits send the `eTag`/`etag` (or a submission's `version`) they loaded as `If-Match`; a 412 means
  someone else changed it first.
- Submit, resubmit, review decisions and upload completion send an `Idempotency-Key`, so a retried
  click can't act twice.
- Files: `uploadFile` asks for a signed upload URL and PUTs the bytes there (with progress), then
  `completeUpload` files it in the library, or `addAttachment` puts it on a submission checklist.
  Downloads and previews ask for a short-lived signed link first, because a link can't carry the token.
- After changing the API, run it and `npm run api:types`; add an alias to `types.ts` for new schemas.

## Structure

```
src/
  api/          API client, generated types, one module per API area
  auth/         signed-in accounts (sessionStore), SessionProvider/useMe, route guard
  app/          shell, sidebar with the account switcher, navigation per capability, config
  components/   shared UI: Badge, Button, PageHeader, Tabs, TypeChip/TagChip, Modal, Switch, Avatar, AsyncState
  lib/          display formats (dates, sizes, terms)
  pages/        one folder per screen; each page has its own CSS module
```

Design tokens (colors, radii) live as CSS variables in `src/index.css`.

Added dependencies: `react-router` (routing) and `lucide-react` (the icon set the design uses).
`openapi-typescript` runs through `npx` for `api:types` (it doesn't support TypeScript 6 as a peer
yet, so it isn't a project dependency).
