# Windows Core live API checks

This console runner exercises the Windows client against the real backend and PostgreSQL. It creates disposable accounts, projects, tasks, tags and entries. **Run only against a dedicated throwaway database and API instance.** The runner requires a loopback URL and uses no stored personal session.

```sh
dotnet run --project windows/Spindle.LiveChecks -- http://127.0.0.1:5287
```

Start a separate `Spindle.Api` instance with its own port and `ConnectionStrings__Default` pointing at a fresh database. Set `Database__MigrateOnStartup=true`, a temporary `Auth__SigningKey`, `ASPNETCORE_ENVIRONMENT=Development` and the required application/mail configuration. Stop this API and drop its database after the run.

Checks cover authentication and refresh rotation; workspaces; clients, projects, tasks and tags; timer and manual split entries; exact lookup and access denial; summary split allocation, detailed and weekly reports; CSV, XLSX and PDF exports; durable offline replay; and conflict recovery after another device moves an entry to another date. Network loss is simulated at the client's transport while writes and reconnects use the real backend.

These checks do not exercise the native Windows UI or operating-system integration.
