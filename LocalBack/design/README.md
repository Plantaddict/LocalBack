# LocalBack design files

Made with the Claude Design canvas. Each screen is a self-contained `.dc.html` file (HTML + inline styles + a small logic block); `canvas.json` records frame sizes and layout.

| Screen | File | Size |
| --- | --- | --- |
| Main window: backup sets | `screens/Main.dc.html` | 900×600 |
| Version history and restore | `screens/History.dc.html` | 900×600 |
| Tray flyout | `screens/Tray.dc.html` | 360×440 |
| Add backup set dialog | `screens/AddSet.dc.html` | 520×800 |
| Free up space dialog | `screens/FreeSpace.dc.html` | 520×600 |

## Tokens

- Font: Segoe UI, system-ui fallback
- Text: #1B1B1B, secondary #4A4A4A
- Surfaces: #FFFFFF, panels #F8F8F8 / #F3F3F3, borders #E3E3E3 / #C8C8C8
- Accent: #0F5FBF, tint #E6EEF9
- Status: green #1F7A4D (tint #E3F1E8), amber #B85C00, red #A12A2A (tint #F6E7E7)
- Radius: 6 px controls, 8–10 px windows and cards
- Buttons: 36 px (32 px in rows); classes `btn-primary`, `btn-secondary`, `btn-outline`, `btn-ghost` in each file's `<helmet><style>`
