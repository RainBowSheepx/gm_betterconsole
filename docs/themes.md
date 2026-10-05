# Themes

BetterConsole comes with four themes — **Dark** (default), **Light**, **Midnight** and **Graphite** —
and picks up your own from the `themes` folder next to `BetterConsole.exe`. Switch with the sun icon
in the top right corner or in *Settings → Appearance*.

| Dark | Light | Midnight |
|---|---|---|
| ![Dark](images/console.png) | ![Light](images/theme-light.png) | ![Midnight](images/theme-midnight.png) |

## Make your own

A theme is a JSON file. Copy one of the two in the release's `themes` folder (`ocean.json`,
`solarized-light.json`) and change the colours. The file name is the theme's name.

```jsonc
{
  "base": "Dark",            // the built-in theme to start from: Dark, Light, Midnight or Graphite
  "Accent": "#E25CC6",       // everything you leave out comes from the base
  "ConsoleBackground": "#0B0710"
}
```

Colours are `#RRGGBB` (or `#AARRGGBB`). Comments and trailing commas are allowed. Pick the theme
from the menu; edits are read when the theme list is opened again.

## Colour names

| Name | Used for |
|---|---|
| `Background` | the window behind everything |
| `Panel` | header, status bar, title bar (Windows 11), dialogs |
| `Surface` | cards, buttons, menus |
| `SurfaceHover`, `SurfaceActive` | the same under the pointer / pressed, neutral badges |
| `Border`, `BorderStrong` | lines around cards and fields; stronger for menus and focus |
| `Text`, `TextSecondary`, `TextMuted` | main text, secondary text, hints |
| `Accent`, `AccentHover` | buttons, the selected tab, links, highlights |
| `AccentSoft` | subtle accent background: selected rows, checked toggles |
| `OnAccent` | text on accent-coloured buttons |
| `Danger`, `DangerSoft` | errors, stop / ban buttons, error badges |
| `Warning`, `WarningSoft` | warnings, values in the auto-completion list |
| `Success`, `SuccessSoft` | "running", "connected" |
| `Info` | informational accents |
| `ConsoleBackground`, `ConsoleText` | the console and code blocks |
| `ConsoleSelection` | selected text in the console |
| `ConsoleCommand` | echo of commands you ran |
| `ConsoleApp` | lines BetterConsole writes itself |
| `InputBackground` | text boxes and the command input |
| `ScrollThumb`, `ScrollThumbHover` | scroll bars |
| `ChartGrid` | chart grid lines |
| `Chart1` … `Chart6` | chart series, avatars |
| `IsDark` (true/false) | dark or light title bar on Windows 10, and how console colours are adjusted |

## Console colours on light themes

Addons choose their `MsgC` colours for a black console, and a light cyan on white is unreadable.
BetterConsole keeps the hue but darkens (on a light background) or lightens (on a dark one) any
colour that does not stand out enough from `ConsoleBackground`, so every theme stays readable.
