# Application identity

Shared branding for the CLI and desktop executables. The layered-backup/checkmark mark extends the application's lime/navy palette; it is product identity, not a backup-status indicator.

- `optibackup.svg`: editable vector artwork.
- `optibackup.png`: transparent 256-pixel desktop navigation artwork.
- `optibackup.ico`: transparent Windows frames at 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels.

Regenerate all three together with `python3 scripts/generate-icons.py`, or verify committed files with `--check`. Verification compares decoded pixel data so different zlib versions do not cause false compression-byte differences. The script contains the matching geometry and uses only the Python standard library, including supersampling and correct alpha coverage. If editing the design, update both its SVG and raster geometry in that script before regenerating.

Both executable projects embed the ICO through `ApplicationIcon`. The desktop also embeds the ICO/PNG as WPF resources, shares the window icon with startup and review dialogs, and uses the PNG in its navigation rail. Windows Terminal and other console hosts manage their own tab/window branding; the CLI's PE icon is used by Explorer and shortcuts.
