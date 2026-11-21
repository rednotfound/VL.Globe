using System.Text;
using System.Text.Json;
using VL.Globe.Engine;
using Xunit.Abstractions;

namespace VL.Globe.Tests.MapLibre;

// MapLibre GL JS's render tests, run as MapLibre Native runs them - the fixtures, MapLibre's own
// thresholds, and an ignore list - for any renderer of ours: the drape (MapLibreRenderFacts, ROADMAP
// 4b) and the GPU path (MapLibreGpuRenderFacts, 4d). Each has its own manifest and results folder.
//
// Each fixture (fetched by tools\Fetch-MapLibreFixtures.ps1 into test\fixtures\maplibre\<tag>) is
// a style.json and expected*.png. A fixture is
//   unsupported - the style uses something outside MapStyle.cs's subset (the reason is reported);
//   passed      - pixelmatch at the fixture's threshold differs on at most `allowed` of the pixels
//                 against one of the expected images, as render.test.ts decides;
//   failed      - otherwise.
// The rules: every supported fixture passes or is in the manifest's ignores with a reason; an
// ignored fixture that passes must leave the list; the number passed never drops below the recorded
// one. That number leaves out passes an empty image would also make (a fixture that expects nothing
// drawn), so drawing nothing cannot raise it. Results (actual.png, diff.png, report.md) go to
// test\fixtures\maplibre\<results>.
internal static class RenderHarness
{
    /// <summary>Draws a strictly parsed style at its centre and zoom: straight RGBA, width x height x pixelRatio.</summary>
    public delegate byte[] Renderer(MapStyleDocument style, int width, int height, double pixelRatio);

    sealed record Manifest(string Tag, int PassedAtLeast, Dictionary<string, string> Ignores);

    enum Outcome { Passed, Failed, Unsupported }
    // Vacuous: a fully transparent image would pass too, so the pass proves nothing about drawing.
    sealed record Result(string Id, Outcome Outcome, double Difference, double Allowed, string? Reason, bool Vacuous = false);

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VL.Globe.nuspec"))) return d.FullName;
        throw new DirectoryNotFoundException("no VL.Globe.nuspec above the test assembly");
    }

    public static void RunAll(ITestOutputHelper output, string manifestName, string resultsName, string title, Renderer render)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MapLibre", manifestName)),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        var root = RepoRoot();
        var fixtures = Path.Combine(root, "test", "fixtures", "maplibre", manifest.Tag);
        Assert.True(Directory.Exists(fixtures),
            $"MapLibre's render-test fixtures are not here: {fixtures}. Fetch them with .\\tools\\Fetch-MapLibreFixtures.ps1 -Tag {manifest.Tag}");
        var results = Path.Combine(root, "test", "fixtures", "maplibre", resultsName);
        if (Directory.Exists(results)) Directory.Delete(results, recursive: true);

        var all = Directory.GetFiles(fixtures, "style.json", SearchOption.AllDirectories)
            .Select(f => Run(fixtures, f, results, render)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

        var passed = all.Where(r => r.Outcome == Outcome.Passed).ToList();
        // the number that may only go up counts the passes an empty image would not also have made
        int drawn = passed.Count(r => !r.Vacuous);
        var failed = all.Where(r => r.Outcome == Outcome.Failed).ToList();
        var unsupported = all.Where(r => r.Outcome == Outcome.Unsupported).ToList();
        Directory.CreateDirectory(results);   // no fixture drew (all threw): the report says why
        File.WriteAllText(Path.Combine(results, "report.md"), Report(title, manifest, all));
        output.WriteLine($"MapLibre {manifest.Tag} on {title}: {passed.Count} passed ({drawn} that an empty image would fail), {failed.Count} failed, {unsupported.Count} unsupported of {all.Count}");
        foreach (var g in unsupported.GroupBy(r => r.Reason).OrderByDescending(g => g.Count()))
            output.WriteLine($"  unsupported {g.Count(),3}  {g.Key}");

        var problems = new List<string>();
        foreach (var r in failed.Where(r => !manifest.Ignores.ContainsKey(r.Id)))
            problems.Add(r.Reason != null ? $"{r.Id} failed: {r.Reason}"
                : $"{r.Id} failed: {r.Difference:P3} of the pixels differ, {r.Allowed:P3} allowed ({resultsName}\\{r.Id}\\diff.png)");
        foreach (var r in passed.Where(r => manifest.Ignores.ContainsKey(r.Id)))
            problems.Add($"{r.Id} passes now: remove it from {manifestName}'s ignores");
        foreach (var id in manifest.Ignores.Keys.Where(id => all.All(r => r.Id != id)))
            problems.Add($"{id} is ignored but is not a fixture");
        if (drawn < manifest.PassedAtLeast)
            problems.Add($"{drawn} passed, fewer than the {manifest.PassedAtLeast} recorded in {manifestName}");
        else if (drawn > manifest.PassedAtLeast)
            output.WriteLine($"  {drawn} passed: raise passedAtLeast in {manifestName} from {manifest.PassedAtLeast}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    static Result Run(string fixtures, string stylePath, string results, Renderer render)
    {
        var dir = Path.GetDirectoryName(stylePath)!;
        var id = Path.GetRelativePath(fixtures, dir).Replace('\\', '/');
        using var json = JsonDocument.Parse(File.ReadAllText(stylePath));
        var test = json.RootElement.TryGetProperty("metadata", out var m) && m.TryGetProperty("test", out var t) ? t : default;
        double Get(string name, double fallback) => test.ValueKind == JsonValueKind.Object && test.TryGetProperty(name, out var v) ? v.GetDouble() : fallback;
        int width = (int)Get("width", 512), height = (int)Get("height", 512);
        double pixelRatio = Get("pixelRatio", 1), allowed = Get("allowed", 0.00025), threshold = Get("threshold", 0.1285);

        byte[] actual;
        try
        {
            if (test.ValueKind == JsonValueKind.Object)
                foreach (var p in test.EnumerateObject())
                    if (p.Name is not ("width" or "height" or "pixelRatio" or "allowed" or "threshold" or "description" or "timeout" or "fadeDuration" or "antialias"))
                        throw new StyleUnsupportedException($"test.{p.Name}");
            var style = MapStyleDocument.Parse(json.RootElement);
            // local://<path> is test/integration/assets/<path>, fetched into <tag>\_assets
            style.Load = url =>
            {
                if (!url.StartsWith("local://")) throw new StyleUnsupportedException($"a resource not in the assets: {url}");
                var file = Path.Combine(fixtures, "_assets", url.Substring("local://".Length).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) throw new StyleUnsupportedException($"an asset not fetched: {url} (add it to Fetch-MapLibreFixtures.ps1 -Assets)");
                return File.ReadAllBytes(file);
            };
            actual = render(style, width, height, pixelRatio);
        }
        catch (StyleUnsupportedException e)
        {
            return new Result(id, Outcome.Unsupported, double.NaN, allowed, e.Message);
        }
        catch (Exception e)
        {
            // a crash is a failure of ours, reported with the fixture, not the end of the run
            return new Result(id, Outcome.Failed, double.PositiveInfinity, allowed, $"threw {e.GetType().Name}: {e.Message}");
        }

        int w = (int)Math.Floor(width * pixelRatio), h = (int)Math.Floor(height * pixelRatio);
        double best = double.PositiveInfinity, blank = double.PositiveInfinity;
        byte[]? bestDiff = null;
        foreach (var expectedPath in Directory.GetFiles(dir, "expected*.png"))
        {
            var (expected, ew, eh) = Png.Read(expectedPath);
            if ((ew, eh) != (w, h)) continue;   // render.test.ts would throw; a size we do not produce cannot match
            var diff = new byte[expected.Length];
            double d = Pixelmatch.Count(actual, expected, diff, w, h, new(Threshold: threshold)) / (double)(w * h);
            if (d < best) { best = d; bestDiff = diff; }
            blank = Math.Min(blank, Pixelmatch.Count(new byte[expected.Length], expected, null, w, h, new(Threshold: threshold)) / (double)(w * h));
        }
        var outDir = Path.Combine(results, id.Replace('/', Path.DirectorySeparatorChar));
        Png.Write(Path.Combine(outDir, "actual.png"), actual, w, h);
        if (bestDiff != null) Png.Write(Path.Combine(outDir, "diff.png"), bestDiff, w, h);
        return new Result(id, best <= allowed ? Outcome.Passed : Outcome.Failed, best, allowed, null, Vacuous: blank <= allowed);
    }

    static string Report(string title, Manifest manifest, List<Result> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# MapLibre GL JS {manifest.Tag} render tests on {title}");
        sb.AppendLine();
        foreach (var g in all.GroupBy(r => r.Outcome)) sb.AppendLine($"- {g.Key}: {g.Count()}");
        sb.AppendLine();
        sb.AppendLine("| fixture | outcome | differs | allowed | note |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in all)
        {
            var note = r.Reason ?? (manifest.Ignores.TryGetValue(r.Id, out var why) ? "ignored: " + why : r.Vacuous ? "an empty image passes too" : "");
            var differs = double.IsNaN(r.Difference) ? "" : double.IsInfinity(r.Difference) ? "no expected image of this size" : r.Difference.ToString("P3");
            sb.AppendLine($"| {r.Id} | {r.Outcome} | {differs} | {r.Allowed:P3} | {note} |");
        }
        return sb.ToString();
    }
}
