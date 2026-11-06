# Changelog

All notable changes to AssetsCraft are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Game palette** in the new project settings (⚙): upload a palette (image, `.hex`, `.gpl`, `.pal`, `.txt`, `.json`) and
  every sprite, tile and animation of that game uses only its colors. Claude is instructed to use `"palette": "game"`,
  and `sprite.mjs render` snaps any other color to the nearest palette color, reporting what it changed.

## [0.1.0] - 2026-11-05

First public release.

### Added
- Chat with Claude Code to create pixel art **sprites** and **tilesets**, with a new version (`-v1`, `-v2`…) for every tweak.
- Linear flow per asset: **Describe → Refine → Animate → Ship to game**, with a clickable stepper.
- **Animations** made from an approved sprite version, which stays fixed as the base.
- **Game style matching:** a sample of the linked game's PNGs (atlases with their `.xml`/`.json`, tilesets) is analysed before drawing.
- Pixel-perfect viewer: integer zoom, frame by frame, backgrounds, pixel grid, full sheet, large preview window.
- **Ship to game** (always confirmed) into the MonoGame `Content/` folder, found through `Content.mgcb`, and **Remove from game**, which restores the file it replaced.
- Attachments marked as *inspiration* or *material to use in the sprite*; video references through the bundled FFmpeg.
- Export: PNG sheet + Aseprite JSON + one GIF per animation.
- Claude usage meter (5h session and weekly limits) and a model picker per conversation.
- Self-contained builds for Windows x64 and Linux x64.

[0.1.0]: https://github.com/P3droSoares/assetscraft/releases/tag/v0.1.0
