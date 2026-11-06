# Contributing to AssetsCraft

Thanks for wanting to help! Bug reports, ideas, docs and code are all welcome.
(Português: pode abrir issues e PRs em português também.)

## Reporting a bug or asking for a feature

Open an [issue](https://github.com/P3droSoares/assetscraft/issues/new/choose) and pick the template.
For bugs, include your OS, the AssetsCraft version and your Claude Code version (`claude --version`), plus what you
clicked and what happened.

## Running from source

Requirements: **.NET 9 SDK**, **Node.js 18+ with npm**, and **Claude Code** installed and logged in.

```bash
git clone https://github.com/P3droSoares/assetscraft.git
cd assetscraft
dotnet run --project src/AssetsCraft.Desktop
```

`scripts/fetch-ffmpeg.sh linux-x64` (or `win-x64`) downloads the bundled FFmpeg. It's optional while developing;
without it, Claude uses the `ffmpeg` on your PATH.

## Project layout

```
src/AssetsCraft.Core/      Claude Code client (stream-json), conversations, sprite metadata, game project I/O
src/AssetsCraft.Desktop/   Avalonia UI (MVVM with CommunityToolkit.Mvvm)
template/workspace/        Copied into every conversation: CLAUDE.md + tools/sprite.mjs (the pixel-art toolchain)
scripts/                   fetch-ffmpeg, publish (self-contained builds)
docs/                      Technical documentation and README images
```

See [docs/TECHNICAL.pt-BR.md](docs/TECHNICAL.pt-BR.md) for the architecture.

## Pull requests

1. Fork the repo and create a branch: `git checkout -b feat/my-change`.
2. Keep changes focused. Run `dotnet build` and make sure it builds without errors.
3. For UI changes, add a screenshot to the PR.
4. Use clear commit messages, for example `feat: ...`, `fix: ...`, `docs: ...`.

### Ground rules
- The app must **never write into the user's game project without an explicit confirmation**, and every write must be undoable.
- Claude only gets the permissions it needs (see `Claude:AllowedTools` in `appsettings.json`). Don't widen them without a good reason.
- Good first contributions: an **English UI** (strings are in Portuguese today), a macOS build, markdown tables in the chat.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
