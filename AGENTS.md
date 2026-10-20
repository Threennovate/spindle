# Agent instructions

Read [docs/principles.md](docs/principles.md) before proposing or designing a feature. It says what Spindle is for: a
high-quality, open-source time tracker built for parallel work with AI agents, with suggestions from local agent logs
that never replace the person's own word, and what it deliberately doesn't do.

Where to find how things work:

- [docs/splits-and-suggestions.md](docs/splits-and-suggestions.md): split entries, reading Claude Code and Codex logs,
  and how split suggestions are made and offered. Read it before touching splits, reports, billing, agreements or
  anything under `apple/SpindleKit/Sources/SpindleKit/Activity` or `windows/Spindle.Activity`.
- [docs/architecture.md](docs/architecture.md): the rules the backend follows (IDs, sync, authentication, reports,
  rates).
- [CONTRIBUTING.md](CONTRIBUTING.md) and the apps' READMEs ([apple](apple/README.md), [windows](windows/README.md),
  [android](android/README.md)): running and testing everything, and how pull requests are made.
- [docs/releasing.md](docs/releasing.md): how the server image and the apps are built, signed and published. Merging a
  Mac version bump with release notes into `main` publishes it.

Anything that adds up time must count each part of a split entry on its own project (`TimeShare.Divide`).
