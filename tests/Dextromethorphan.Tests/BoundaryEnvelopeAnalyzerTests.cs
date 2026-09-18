using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using NAudio.Wave;

namespace Dextromethorphan.Tests;

public sealed class BoundaryEnvelopeAnalyzerTests
{
    [Fact]
    public async Task ReadsRealPcmBoundariesWithoutModifyingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "out.wav"); var b = Path.Combine(root, "in.wav");
            void Write(string path, bool outgoing)
            {
                using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(8000, 2));
                var buffer = new float[16000];
                for (var second = 0; second < 40; second++)
                {
                    var gain = outgoing
                        ? second >= 37 ? .01 : .4
                        : second < 2 ? 0 : .4;
                    for (var i = 0; i < 8000; i++)
                    {
                        buffer[i * 2] = (float)(gain * Math.Sin(i * Math.PI / 10));
                        buffer[i * 2 + 1] = -buffer[i * 2];
                    }
                    writer.WriteSamples(buffer, 0, buffer.Length);
                }
            }
            Write(a, true); Write(b, false);
            var before = File.ReadAllBytes(a);
            var analyzer = new BoundaryEnvelopeAnalyzer();
            var plan = await analyzer.AnalyzeAsync(new Track { Path = a, Title = "Outgoing" }, new Track { Path = b, Title = "Incoming" }, 8, TestContext.Current.CancellationToken);
            Assert.InRange(plan.Seconds, .25, .5);
            Assert.InRange(plan.TrimTrailingSeconds, 2.4, 2.6);
            Assert.InRange(plan.SkipLeadingSeconds, 1.8, 2.0);
            Assert.Equal(before, File.ReadAllBytes(a));
            var cached = await analyzer.AnalyzeAsync(new Track { Path = a, Title = "Outgoing" }, new Track { Path = b, Title = "Incoming" }, 8, TestContext.Current.CancellationToken);
            Assert.Equal(plan, cached);
        }
        finally { Directory.Delete(root, true); }
    }
}
