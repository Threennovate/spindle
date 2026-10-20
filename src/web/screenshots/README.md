# Screenshots for the docs and the README

`npm run screenshots` (here, in `src/web`) fills a new workspace with the made-up data the Spindle website uses
everywhere (Acme API, Acme Web, Northwind and Internal; Emma, Noah and Sam) and takes the pictures the docs show.
They are written to `artifacts/docs-screenshots` at the repository root, or to the folder in `DOCS_SCREENSHOTS`.
The website (`Threennovate/spindle-site`) keeps them in `public/docs/`, converted to WebP.

Needs the local PostgreSQL and Mailpit (`../../scripts/local-db.sh`). The API runs against a database of its own,
`SpindleDocs`, which it creates and migrates when it starts, so the development database isn't touched. Each run
creates a new account, so nothing depends on what is already there.

`readme.screenshots.ts` takes the pictures in the repository's README, in light and dark, into
`artifacts/readme-screenshots` (or `README_SCREENSHOTS`); copy them to `.github/assets/`. Run it on a weekday after
Monday, in the afternoon, so the week and today have their entries.

On a local PostgreSQL created before the rename, set `POSTGRES_USER=threetrack`.

Take them again when a screen they show changes.
