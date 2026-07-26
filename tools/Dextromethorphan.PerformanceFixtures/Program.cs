using System.Globalization;
using System.IO;

namespace Dextromethorphan.PerformanceFixtures;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            var progress = new Progress<FixtureProgress>(value =>
            {
                Console.Write($"\r{value.Stage,-18} {value.Completed,7:N0}/{value.Total,7:N0}");
                if (value.Completed == value.Total) Console.WriteLine();
            });
            var manifest = await new PerformanceFixtureGenerator().GenerateAsync(options, progress);
            Console.WriteLine($"Fixture:  {Path.GetFullPath(options.OutputRoot)}");
            Console.WriteLine($"Tracks:   {manifest.TrackCount:N0}");
            Console.WriteLine($"Albums:   {manifest.AlbumCount:N0}");
            Console.WriteLine($"Artists:  {manifest.ArtistCount:N0}");
            Console.WriteLine($"Artwork:  {manifest.ArtworkCount:N0}");
            Console.WriteLine($"Playlists:{manifest.PlaylistCount,7:N0}");
            Console.WriteLine($"Content:  {manifest.ContentSha256}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }
    }

    private static PerformanceFixtureOptions Parse(string[] args)
    {
        int? trackCount = null;
        string? output = null;
        var seed = PerformanceFixtureOptions.DefaultSeed;
        var force = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--tracks":
                    trackCount = int.Parse(RequireValue(args, ref index, "--tracks"), CultureInfo.InvariantCulture);
                    break;
                case "--output":
                    output = RequireValue(args, ref index, "--output");
                    break;
                case "--seed":
                    seed = int.Parse(RequireValue(args, ref index, "--seed"), CultureInfo.InvariantCulture);
                    break;
                case "--force":
                    force = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (trackCount is not (10_000 or 50_000))
            throw new ArgumentException("--tracks must be 10000 or 50000.");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--output is required.");
        return new PerformanceFixtureOptions(trackCount.Value, output, seed, force);
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (++index >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Generate a deterministic Dextromethorphan performance library.");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project tools/Dextromethorphan.PerformanceFixtures -c Release -- \\");
        Console.WriteLine("    --tracks 10000 --output <directory> [--seed 20260725] [--force]");
        Console.WriteLine();
        Console.WriteLine("--force only replaces a directory previously created by this generator.");
    }
}
