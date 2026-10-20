# Spindle brand

The name is **Spindle**, one word. The mark is a spindle drawn from bars of time: a shaft at the top and bottom, and
bars wound between them that grow towards the middle. The longest bar, in marigold, is your hour, where parallel work
comes together.

The mark stands on its own, with no frame, in the app and on the web. It takes a rounded midnight tile only where it needs a
ground of its own: the favicon and the app icons.

| File | Use |
| --- | --- |
| `spindle-mark.svg`, `spindle-mark-dark.svg` | The mark on its own, on light and on dark backgrounds |
| `spindle-mark-compact.svg` | Three bars instead of five, for 24 px and smaller, where five would run together |
| `spindle-logo-light.svg`, `spindle-logo-dark.svg` | Mark and name, on light and on dark backgrounds |
| `spindle-tile.svg`, `spindle-tile-1024.png` | The mark on the midnight tile, e.g. avatars |
| `spindle-app-icon.svg`, `spindle-app-icon-1024.png` | Square app icon for iOS; the system rounds the corners |
| `spindle-mac-icon.svg` | The Mac app icon: a rounded tile with a shadow on Apple's icon grid, since macOS draws icons as they are |
| `MenuBarMark` (in the Mac app's assets) | The compact mark in one colour, cropped to its bars, for the menu bar |

The same drawing gives the web app's favicon (the compact mark on the tile), touch icon and install icons in
`src/web/public`, and the Apple apps' icons and logos. After a change to the design, `build.mjs` draws them all again.
The web app draws the mark in `src/web/src/components/logo.tsx` from the same numbers.

The picture at the top of the repository's README is `readme/header.html`, the App Store header's split entry as a
running timer, in a light and a dark version. `cd src/web && node ../../branding/readme/render.mjs` draws both into
`readme/out/`; copy them to `.github/assets/readme-header-{light,dark}.png`.

## Colors

| | Hex | Where |
| --- | --- | --- |
| Midnight | `#0E1322` | The mark and name on light backgrounds; the tile; dark backgrounds |
| Marigold | `#F7BE2B` | The middle bar on dark backgrounds; buttons in dark mode |
| Deep marigold | `#E79D00` | The middle bar on light backgrounds, where the lighter marigold would wash out |
| Track grey | `#E9ECF2` | The bars and name on dark backgrounds |
| Graphite | `#141518` | The dark theme of the web app; text and symbols on marigold in the apps |
| Bronze | `#8F5D04` | Text, symbols and links in the brand colour on light backgrounds |
| Amber | `#B07000` (`#C27C00` in dark mode) | The accent of controls in the Apple apps: switches, segmented controls, selections |

Midnight is a deep blue ink. Next to it, marigold glows without reading as black-and-yellow hazard tape, and the
blue reads as calm and trustworthy to the people who buy a time tracker. Dark brand materials (video, social, GitHub)
and the icon tile sit on midnight. Light materials sit on white or a soft paper `#F7F6F3`, with midnight type. The
earlier warm ink `#120F0B` is no longer used.

The apps don't paint midnight behind the work. The Mac and iPhone apps keep the system's own window, sidebar and
list backgrounds in light and dark mode, and the web app's dark theme is graphite, near-neutral and a hair cool (hue
271). The brand shows in marigold, the mark and the shape of the project blocks instead.

Marigold is used sparingly, like the one marigold bar in the mark: the main button, the timer's start button, a
selected split part. It is never a ground and never a wash: a tint of marigold turns olive
over graphite and beige over white, so cards and highlighted rows use a neutral tint with one marigold detail, such as
their symbol or button. Charts are neutral, and the projects' colours carry them.

Buttons are marigold `#F0B21B` (`#F7BE2B` in dark mode) with graphite on them (10:1). Marigold is too light to read
as text on white, so text, icons and links in the primary colour use bronze `#8F5D04` (5.6:1, `--primary-text` on the
web, `Color.brandText` in the apps); in dark mode they are marigold. Controls that the system draws with white on
the accent, such as a segmented control's chosen segment, use amber in the Apple apps (`AccentColor`), which keeps
that white readable (4:1, like Apple's own blue); the Mac windows don't tint plain buttons with it. Warnings use
orange, so they don't read as the brand colour.

Calendar blocks are tonal: one shape in the project's colour, light enough to read on, held by a hairline in that
colour, with the project named in its colour. There is no coloured stripe down the side. A split entry takes the
colour of its largest part and shows all its parts as a thin thread along the bottom, each as long as its share, the
same split bar the entry lists use. In the calendar, now is marigold: today's date and the line across the day.

The name is set in Fraunces at weight 450 with its SOFT axis at 100, so the terminals are round like the ends of the
bars, and WONK off; letter spacing −1%, in midnight (or track grey on dark). Only the name uses Fraunces: the web interface, the
tagline and everything else stay in Geist, and the Apple apps use the system font. In the lockup the mark stands 1.1 × the type size tall, a touch below the
middle of the line (0.04 em), 0.175 em from the name. `fonts/Fraunces-Spindle.ttf` is a static instance of the
variable font at those settings (optical size 72), which `build.mjs` draws the name from; Fraunces is under the SIL Open
Font License (`fonts/OFL.txt`).

## License

The name, the mark, the logos, the icons and the launch video are not under the AGPL: see
[TRADEMARKS.md](../TRADEMARKS.md). The fonts in `fonts/` are under the SIL Open Font License (`fonts/OFL.txt`).
