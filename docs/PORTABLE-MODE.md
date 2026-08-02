# Portable mode

Dextromethorphan can keep all of its app data beside the executable. This is intended for a writable portable ZIP or removable drive, not an installation under `Program Files`.

To enable it, either:

- start `Dextromethorphan.exe --portable`, or
- create an empty file named `portable.mode` beside `Dextromethorphan.exe`.

The app then stores settings, the SQLite index, artwork cache, logs, backups, and scan checkpoints in a `data` folder beside the executable. Music files are never copied or modified by portable mode.

The `DEXTROMETHORPHAN_DATA_ROOT` environment variable remains the explicit override and takes priority over portable mode.
