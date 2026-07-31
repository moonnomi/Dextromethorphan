# App-data layout and uninstall behavior

The default data root is:

```text
%APPDATA%\Dextromethorphan
```

For tests, `DEXTROMETHORPHAN_DATA_ROOT` selects a separate root for that process.

| Entry | Purpose |
|---|---|
| `settings.json` | User settings; saved atomically with a fallback copy |
| `library.db` | SQLite library index and user state |
| `artwork\` | Extracted artwork and persistent thumbnail variants |
| `logs\` | Rotating structured JSONL application logs |
| `backups\` | Rotating database migration/recovery backups |
| `recovery-state-*.json` | Best-effort state captured before a corrupt database rebuild |

Music files are not copied into app data and are not modified by uninstall.

The installer/uninstaller must leave the entire data root in place by default. Removing the application binaries must never silently delete the database, playlists, ratings, bookmarks, settings, backups, or artwork cache. A future “remove my data” option must be explicit, unchecked by default, identify the exact path, and require confirmation. Until that option exists, users can remove the data root manually after exporting a `.dexbackup`.
