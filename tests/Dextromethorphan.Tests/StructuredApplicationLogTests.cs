using System.Text.Json;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class StructuredApplicationLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LogIsStructuredRotatedAndRetentionBounded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var log = new StructuredApplicationLog(
            paths,
            maximumFileBytes: 64 * 1024,
            retainedFiles: 2);
        var payload = new string('x', 2_048);
        for (var index = 0; index < 180; index++)
            log.Write(
                ApplicationLogLevel.Information,
                "test",
                "event",
                new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["payload"] = payload
                });

        await log.CompleteAsync(cancellationToken);

        var files = Directory.GetFiles(paths.Logs, "app-*.jsonl");
        Assert.InRange(files.Length, 1, 2);
        var entries = files
            .SelectMany(File.ReadLines)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.NotEmpty(entries);
            Assert.All(entries, entry =>
            {
                Assert.Equal("Information", entry.RootElement.GetProperty("level").GetString());
                Assert.Equal("test", entry.RootElement.GetProperty("category").GetString());
                Assert.Equal("event", entry.RootElement.GetProperty("operation").GetString());
            });
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    [Fact]
    public async Task CompletionDoesNotDependOnTheCreatingSynchronizationContext()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new NeverDispatchSynchronizationContext());
                var paths = new AppPaths(Path.Combine(_root, "sync-context"));
                var log = new StructuredApplicationLog(paths);
                log.Write(
                    ApplicationLogLevel.Information,
                    "test",
                    "dispatcher-shutdown");
                log.CompleteAsync().GetAwaiter().GetResult();
                completion.SetResult(null);
            }
            catch (Exception exception)
            {
                completion.SetResult(exception);
            }
        })
        {
            IsBackground = true
        };
        thread.Start();

        var error = await completion.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Null(error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class NeverDispatchSynchronizationContext :
        SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }
    }
}
