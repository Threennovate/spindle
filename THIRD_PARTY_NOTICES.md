# Third-party software

Spindle is built on the open-source projects below, each under its own license. This lists what ships in Spindle's
server, web app and Mac, iPhone, Windows and Android apps; their own dependencies come in through NuGet, npm, Swift
Package Manager and Gradle, with their licenses in each package. Tools used only to build or test Spindle are not listed.

## Fonts

| Font | Where | License |
| --- | --- | --- |
| [Geist](https://github.com/vercel/geist-font) | The web app, and PDF exports from the server | SIL Open Font License 1.1 (`src/Spindle.Api/Reports/Export/Fonts/OFL.txt`) |
| [Fraunces](https://github.com/undercasetype/Fraunces) | The Spindle wordmark and the web app | SIL Open Font License 1.1 (`branding/fonts/OFL.txt`) |

## Server (.NET)

| Package | License |
| --- | --- |
| ASP.NET Core and Entity Framework Core (Microsoft.AspNetCore.*, Microsoft.Extensions.*) | MIT |
| [Npgsql Entity Framework Core provider](https://github.com/npgsql/efcore.pg) | PostgreSQL License |
| [NodaTime](https://nodatime.org) | Apache-2.0 |
| [MailKit](https://github.com/jstedfast/MailKit) | MIT |
| [ClosedXML](https://github.com/ClosedXML/ClosedXML) | MIT |
| [PDFsharp and MigraDoc](https://github.com/empira/PDFsharp) | MIT |

## Web app

| Package | License |
| --- | --- |
| [React](https://react.dev) and React DOM | MIT |
| [TanStack Router and Query](https://tanstack.com) | MIT |
| [Radix UI](https://www.radix-ui.com) | MIT |
| [cmdk](https://github.com/pacocoursey/cmdk) | MIT |
| [sonner](https://github.com/emilkowalski/sonner) | MIT |
| [react-day-picker](https://daypicker.dev) | MIT |
| [date-fns](https://date-fns.org) and @date-fns/tz | MIT |
| [@microsoft/signalr](https://github.com/dotnet/aspnetcore) | MIT |
| [openapi-fetch](https://openapi-ts.dev) | MIT |
| [lucide-react](https://lucide.dev) | ISC |
| [class-variance-authority](https://cva.style) | Apache-2.0 |
| cn, tw-animate-css | MIT |
| @fontsource-variable/geist, @fontsource-variable/fraunces | SIL Open Font License 1.1 |

## Apple apps

| Package | License |
| --- | --- |
| [Sparkle](https://sparkle-project.org) (Mac updates) | MIT, with the licenses of the code it includes |
| [Swift OpenAPI Runtime](https://github.com/apple/swift-openapi-runtime) and URLSession transport | Apache-2.0 |
| [Swift HTTP Types](https://github.com/apple/swift-http-types) | Apache-2.0 |
| [Swift Collections](https://github.com/apple/swift-collections) | Apache-2.0 |

## Windows app

| Package | License |
| --- | --- |
| [Avalonia](https://avaloniaui.net) (UI, Fluent theme, headless testing) | MIT |
| [Velopack](https://velopack.io) (installer and updates) | MIT |
| [ASP.NET Core SignalR client](https://github.com/dotnet/aspnetcore) (live sync) | MIT |
| [Inter](https://github.com/rsms/inter) through Avalonia.Fonts.Inter (where Segoe UI isn't installed) | SIL Open Font License 1.1 |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (icon outlines in `windows/Spindle.Windows/Theme/Icons.axaml`) | MIT |

## Android app

| Package | License |
| --- | --- |
| [Jetpack Compose](https://developer.android.com/jetpack/compose), Material 3 and AndroidX (Activity, Lifecycle, Core, Room, WorkManager) | Apache-2.0 |
| [Kotlin coroutines](https://github.com/Kotlin/kotlinx.coroutines) and [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | Apache-2.0 |
| [OkHttp](https://square.github.io/okhttp/) | Apache-2.0 |
| [SignalR Java client](https://github.com/dotnet/aspnetcore) (live sync) | MIT |
