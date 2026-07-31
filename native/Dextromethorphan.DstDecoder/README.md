# Native DST decoder shim

This small C ABI wrapper exposes `dst-decoder` 0.1.2 to the managed audio pipeline. Normal .NET builds use the checked-in release DLL under `Dextromethorphan.Infrastructure/runtimes`; Rust is needed only when updating that binary.

```powershell
cargo build --manifest-path native/Dextromethorphan.DstDecoder/Cargo.toml --release --locked
```

After qualification, copy `target/release/dextromethorphan_dst.dll` to `src/Dextromethorphan.Infrastructure/runtimes/win-x64/native/` and verify its SHA-256 in the DST qualification report.

The wrapper and upstream decoder are Apache-2.0. See the packaged license and source attribution in `licenses`.
