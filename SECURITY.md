# Security

## Reporting a vulnerability

Please email **hello@threennovate.com** and don't open a public issue. Tell us what you found, how to reproduce it,
and what someone could do with it. We'll confirm we got it within three working days and keep you posted until it is
fixed. If you'd like to be credited when we announce the fix, say how.

Please give us a reasonable time to fix a problem before you talk about it publicly, and don't access, change or
delete other people's data while testing; use an account of your own, ideally on a server you run yourself.

## What counts

Anything in this repository: the server and its API, the web app, the Mac, Windows, iPhone and Android apps, and the
scripts that build and release them. That includes Spindle Cloud (`app.spindletrack.com`), which runs this code. Weaknesses in a
self-hosted server's own setup (its proxy, database or mail) belong to whoever runs it.

## Supported versions

Fixes go into `main` and the next release. Spindle Cloud runs the latest `main`; self-hosters should update to the
latest version. The Mac and Windows apps update themselves; the iPhone and Android apps come from their stores.
