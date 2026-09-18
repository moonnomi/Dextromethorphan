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
}
