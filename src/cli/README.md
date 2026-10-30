# CLI

Executable entrypoint and user interaction for OptiScaler Backup. No backup or package transaction logic is implemented here.

- `Program.Main`: argument dispatch, data-home/session setup, pending recovery and error reporting.
- `Application.Run`: scriptable commands, structured output, operation previews and explicit confirmations.
- `Application.Menu`: game navigation, managed-file selection, snapshot actions and reviewed ZIP mappings.

Running without arguments opens menus when input is interactive; `help` lists commands. `--home` selects a catalog home. `--yes` accepts the final operation confirmation; overlap and externally edited package files still require their specific override flags. Startup update checks occur only for interactive menu sessions.

Build/run instructions and complete workflows are in the [root README](../../README.md).
