The app icon. `spec/icon.png` (1254 px) is the source; these are made from it:

- `OctezDesk.ico`: the exe, window and taskbar icon, with PNG entries at 16, 20, 24, 32, 40, 48, 64, 128 and 256 px.
- `icon-256.png`: the icon shown inside the app (Welcome page, dashboard header).

To regenerate them, resize the source with high-quality bicubic scaling and pack the PNGs into an ICO (6-byte header, a 16-byte directory entry per image, then the PNG data; width/height 0 means 256).
