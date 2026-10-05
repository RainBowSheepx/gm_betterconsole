# Building

## What you need

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
- Visual Studio 2022 with **Desktop development with C++** (for the native module; CMake and Ninja
  come with it)
- Git

## Get the source

```powershell
git clone --recursive https://github.com/RainBowSheepx/gm_betterconsole.git
cd gm_betterconsole
```

(Already cloned without `--recursive`? `git submodule update --init`.)

## Layout

| Folder | |
|---|---|
| `src/BetterConsole.App` | the WPF application |
| `src/BetterConsole.Core` | pseudo console, VT emulator, line classifier, pipe server, server control |
| `src/BetterConsole.Sdk` | the plugin interfaces |
| `native/` | `gmsv_betterconsole` (C++), built with CMake |
| `addon/betterconsole` | the companion Lua addon |
| `samples/` | the example addon tab, the sample plugin, two themes |
| `tests/` | unit tests of the emulator and the classifier |
| `scripts/` | build, package and release scripts |

## Build and run

```powershell
.\scripts\build-native.ps1                  # native\build\x64 and x86
dotnet run --project src\BetterConsole.App
```

The native module and the Lua addon are embedded into `BetterConsole.exe` at build time, so build
the module first. Without it the app still builds (it then cannot install the module and says so).

## Tests

```powershell
dotnet test tests\BetterConsole.Tests
```

The tests replay what ConPTY sends (colours, the soft-wrap trick, code page 437 mojibake) and the
error formats srcds prints.

## Package a release locally

```powershell
.\scripts\package.ps1 -Version 0.2.0
```

Builds the module, runs the tests, publishes the app (self-contained single file and a small
framework-dependent one), builds the sample plugin and writes `dist\*.zip` and `SHA256SUMS.txt`.

## Publish a release

1. Add a section `## v0.2.0 - <date>` at the top of `CHANGELOG.md`.
2. Commit, then tag and push:
   ```powershell
   git tag -a v0.2.0 -m "BetterConsole 0.2.0"
   git push origin main v0.2.0
   ```
3. The **Release** workflow builds everything on GitHub and publishes the release with the notes
   from the changelog.

Run the workflow by hand (*Actions → Release → Run workflow*) to get the files as an artifact
without publishing.

## Screenshots of the documentation

`BetterConsole.exe --ui-script file.txt` drives the window from a script and renders screenshots
without touching the mouse or keyboard. The commands are listed in
[`src/BetterConsole.App/Services/UiScript.cs`](../src/BetterConsole.App/Services/UiScript.cs).
