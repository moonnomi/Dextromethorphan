using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class CrossfadeOutputHardwareTests
{
    [Fact]
    [Trait("Category", "AudioHardware")]
    public async Task SeekPreservesPreparedNextTrackAndSubmitsMixedAudio()
    {
        if (Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_RUN_CROSSFADE_TEST") != "1") return;
        var path = Path.Combine(Path.GetTempPath(), $"dextro-crossfade-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                for (var i = 0; i < 48000 * 8; i++)
                {
                    var sample = (float)(.01 * Math.Sin(i * 2 * Math.PI * 440 / 48000));
                    writer.WriteSample(sample); writer.WriteSample(sample);
                }
            await using var engine = new WasapiAudioEngine();
            await engine.SetPlaybackOptionsAsync(new AudioPlaybackOptions { TransitionMode = TransitionMode.Crossfade, CrossfadeSeconds = 2 });
            var track = new Track { Path = path, Title = "Synthetic crossfade probe", Duration = TimeSpan.FromSeconds(8) };
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.TrackTransitioned += (_, args) => completed.TrySetResult(args.Crossfaded);
            await engine.LoadAsync(track);
            await engine.QueueNextAsync(track with { Id = 2, Title = "Synthetic next probe" });
            await engine.SeekAsync(TimeSpan.FromSeconds(4));
            Assert.Contains("next queued", engine.Diagnostics!.Reason);
            await engine.PlayAsync();
            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
            await engine.PauseAsync();
            Assert.True(engine.Diagnostics!.OutputMeasurement!.Frames > 0);
            Assert.True(engine.Diagnostics.CrossfadeMeasurement!.MixedFrames > 0);
            Assert.Equal(0, engine.Diagnostics.OutputMeasurement.NonFiniteSamples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "AudioHardware")]
    public async Task DynamicWaveformPlanAdvancesBothCuePointsAndReachesWasapi()
    {
        if (Environment.GetEnvironmentVariable("DEXTROMETHORPHAN_RUN_CROSSFADE_TEST") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), $"dextro-smart-crossfade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outgoingPath = Path.Combine(root, "outgoing.wav");
        var incomingPath = Path.Combine(root, "incoming.wav");
        try
        {
            WriteBoundaryWave(outgoingPath, leadingSilenceSeconds: 0, trailingSilenceSeconds: 1.5);
            WriteBoundaryWave(incomingPath, leadingSilenceSeconds: .6, trailingSilenceSeconds: 0);
            await using var engine = new WasapiAudioEngine();
            var token = TestContext.Current.CancellationToken;
            await engine.SetPlaybackOptionsAsync(new AudioPlaybackOptions
            {
                TransitionMode = TransitionMode.Crossfade,
                CrossfadeSeconds = 1,
                CrossfadeShape = new CrossfadeShape
                {
                    DynamicEnabled = true,
                    Curve = CrossfadeCurve.EqualPower
                }
            }, token);
            var outgoing = new Track { Path = outgoingPath, Title = "Dynamic outgoing", Duration = TimeSpan.FromSeconds(4) };
            var incoming = new Track { Path = incomingPath, Title = "Dynamic incoming", Duration = TimeSpan.FromSeconds(4) };
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.TrackTransitioned += (_, args) => completed.TrySetResult(args.Crossfaded);
            await engine.LoadAsync(outgoing, token);
            await engine.QueueNextAsync(incoming, token);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!(engine.Diagnostics?.Reason.Contains("Boundary plan:", StringComparison.Ordinal) ?? false)
                   && DateTime.UtcNow < deadline)
                await Task.Delay(25, token);
            Assert.Contains("outgoing tail advanced", engine.Diagnostics!.Reason);
            Assert.Contains("incoming silence skipped", engine.Diagnostics.Reason);

            await engine.PlayAsync(token);
            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(8), token));
            await engine.PauseAsync(token);
            Assert.True(engine.Diagnostics!.CrossfadeMeasurement!.MixedFrames > 0);
            Assert.True(engine.Diagnostics.OutputMeasurement!.Frames > 0);
            Assert.Equal(0, engine.Diagnostics.OutputMeasurement.NonFiniteSamples);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WriteBoundaryWave(
        string path,
        double leadingSilenceSeconds,
        double trailingSilenceSeconds)
    {
        const int sampleRate = 48_000;
        const int seconds = 4;
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        for (var frame = 0; frame < sampleRate * seconds; frame++)
        {
            var time = frame / (double)sampleRate;
            var silent = time < leadingSilenceSeconds || time >= seconds - trailingSilenceSeconds;
            var sample = silent ? 0f : (float)(.12 * Math.Sin(frame * 2 * Math.PI * 440 / sampleRate));
            writer.WriteSample(sample);
            writer.WriteSample(sample);
        }
    }
}
