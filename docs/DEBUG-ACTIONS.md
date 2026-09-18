# Debug right-click actions

Enable **Settings → Diagnostics → Enable debug right-click actions**. The option is saved immediately and is off by default. Open a track, queue-entry or collection context menu and expand **Debug**.

- **Open file in Explorer** reveals the underlying media file (also for CUE tracks).
- **Copy file path** copies that path to the clipboard only when requested.
- **Inspect track metadata** shows the library identity, format metadata, CUE boundaries and ReplayGain fields.
- **Test audio decoder** opens a separate decoder and reads short head/tail sections. It neither plays audio nor changes tags, the queue or library. Collection actions explicitly inspect their representative track, not the whole collection.
- **Show live output diagnostics** opens the existing output measurement panel.

Reports are selectable and can be saved as JSON. They contain local paths and are not automatically uploaded. A short decoder test is not a full-file integrity check or a physical-output test. One decoder test runs at a time; cancellation is cooperative, and an unresponsive native/network read may outlast the 15-second cancellation request. Closing the report cancels further work when that read returns.
