# ReplayGain analysis and controls

Date: 2026-07-31

This report is the evidence for DSP-003, DSP-004, DSP-006, and DSP-008.

## Analyzer

The offline analyzer implements the mono/stereo integrated-loudness path from
ITU-R BS.1770 and EBU R128:

- 48 kHz K-weighting with the specified pre-filter and RLB stages;
- 400 ms measurement blocks with 75% overlap;
- the -70 LUFS absolute gate and -10 LU relative gate;
- separate track measurements and a block-weighted album measurement;
- decoded sample-peak measurement;
- a -18 LUFS ReplayGain 2.0 target.

Official algorithm references:

- [ITU-R BS.1770-5](https://www.itu.int/rec/R-REC-BS.1770-5-202311-I/en)
- [EBU R128 loudness resources](https://tech.ebu.ch/loudness/)

Album values are committed only when every available track in that album was
measured successfully. A failed file can therefore never produce a misleading
partial-album gain. Existing embedded gain values are retained.

## File and playback safety

Analysis opens media read-only and writes calculated values only to the local
SQLite index. It never writes tags or changes timestamps. The job runs off the
UI thread, is cancellable, reports progress, and automatically waits while the
audio engine is playing or buffering so it does not compete with playback.
Completed database batches are retained after cancellation so a later run can
resume the missing work.

An end-to-end test copies legal generated WAV files into a temporary library,
hashes and timestamps them, performs track/album analysis, validates the stored
database values and user rating/play-count fields, and proves the media files
are byte-for-byte unchanged. A second test holds the engine in Playing state,
cancels the waiting job, and verifies no analysis fields or media bytes change.

## Settings

The Playback settings page now exposes:

- Off, Track, and Album ReplayGain modes;
- a -20 dB to +20 dB preamp;
- clipping-prevention state;
- explicit Save processing action;
- Analyze missing and Cancel actions with progress and status.

The existing limiter is deliberately and consistently labelled **sample-peak
guard**. It is not presented as an oversampled true-peak limiter. DoP playback
continues to bypass ReplayGain and all other DSP.

```powershell
dotnet test tests/Dextromethorphan.Tests/Dextromethorphan.Tests.csproj `
  --filter "FullyQualifiedName~ReplayGainAnalysisTests|FullyQualifiedName~DspQualificationTests|FullyQualifiedName~SettingsWindowSmokeTests"
```

Focused result on 2026-07-31: 9/9 analyzer, safety, DSP, and Settings cases
passed.
