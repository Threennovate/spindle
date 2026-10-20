Spindle for Windows - preview build

Extract the complete ZIP into a folder and run Spindle.exe. Keep all files together, including the native runtime DLLs. .NET does not need to be installed.

This preview is unsigned. Windows may show an unknown-publisher or SmartScreen prompt. Only run the package you received from the project's developer.

Use a backend built from the feat/windows-desktop branch. The application requires the branch's exact-entry endpoint (GET /api/workspaces/{workspaceId}/time-entries/{entryId}) for offline reconciliation. Do not use this preview with an older production backend lacking that endpoint.

Sign in to your hosted or self-hosted server, or choose Demo to explore sample data. Demo does not connect to a server. Launch at login and local agent access are opt-in. Refresh tokens are kept in Windows Credential Manager.

This is a portable build without an installer or automatic updates; released versions come from the installer at /download/windows. Windows desktop smoke testing is still required for tray, credentials, shortcuts, startup; these packages were cross-published on macOS.
