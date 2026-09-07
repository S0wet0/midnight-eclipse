# Midnight Eclipse

A glass status bar for [Zebar](https://github.com/glzr-io/zebar), built for the [komorebi](https://github.com/LGUG2Z/komorebi) tiling window manager on Windows 11: workspaces, layouts, an app list that mirrors the taskbar, media, tray and system readings in one 44px strip.

## Install

```powershell
git clone https://github.com/S0wet0/midnight-eclipse.git
cd midnight-eclipse
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

The installer builds the helper programs in `helpers/` from source with the C# compiler that ships with Windows, copies `pack/` into Zebar's folder with this machine's paths filled in, and sets Zebar to start the bar.

komorebi needs to leave room for the bar: `"global_work_area_offset": { "left": 0, "top": 44, "right": 0, "bottom": 44 }`, plus an ignore rule for `zebar.exe`.

## License

[MIT](LICENSE) © 2026 S0wet0
