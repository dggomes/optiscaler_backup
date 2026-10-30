# CLI

Executable entrypoint and user interaction for OptiScaler Backup. No backup or package transaction logic is implemented here.

- `Program.Main`: argument dispatch, data-home/session setup, pending recovery and error reporting.
- `Application.Run`: scriptable commands, structured output, operation previews and explicit confirmations.
- `Application.Menu`: game navigation, managed-file selection, snapshot actions and reviewed ZIP mappings.

Running without arguments opens menus when input is interactive; explicit `menu` also supports piped input. `help` lists commands. Piped menu sessions and other scripted commands skip startup update checks.

The game list accepts numbers or case-insensitive partial names. A unique name match opens directly; multiple matches narrow the list; `*` clears the filter. Opening a game detects new standard managed-file candidates, offers optional extra relative/absolute paths, displays the complete proposed batch with proxy evidence and external paths, then applies it with one confirmation. The manual detection action uses the same flow; there are no file-by-file approvals.

`--home` selects a catalog home. `--yes` accepts the final operation confirmation; overlap and externally edited package files still require their specific override flags.

Build/run instructions and complete workflows are in the [root README](../../README.md).
