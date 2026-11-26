using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Bobar.App;
using Bobar.App.ViewModels;
using Bobar.App.Views;
using Bobar.Core;

namespace Bobar.Shots;

/// <summary>
/// Renders the main window offscreen (Avalonia headless + Skia) and saves PNGs, so a session without a screen can
/// check visual changes. The run is copied into a temp data dir first, so the real runs, trash and gui.json are never
/// touched; config.json and keys.env are read from the real config dir as usual.
///
///   dotnet run --project tools/Bobar.Shots -- --run &lt;id or prefix&gt; [--zoom 1.5,2] [--size 1280x820] [--expand] [--out dir]
///
/// One PNG per zoom level (default 1), named &lt;run&gt;@&lt;zoom&gt;.png. --expand opens every round, finding and hindsight
/// row first. Use a tall --size (e.g. 1280x3000) to capture a long body without scrolling.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var run = Arg(args, "--run");
        if (run is null)
        {
            Console.Error.WriteLine("usage: bobar-shots --run <id or prefix> [--zoom 1,1.5] [--size 1280x820] [--expand] [--out dir]");
            return 2;
        }
        var zooms = (Arg(args, "--zoom") ?? "1").Split(',').Select(z => double.Parse(z, CultureInfo.InvariantCulture)).ToList();
        var size = (Arg(args, "--size") ?? "1280x820").Split('x').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var outDir = Path.GetFullPath(Arg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "bobar-shots"));
        var expand = args.Contains("--expand");

        // Copy the run into a throwaway data dir.
        var real = BobarPaths.FromEnvironment();
        var source = Directory.Exists(real.RunsDir)
            ? Directory.GetDirectories(real.RunsDir).Select(Path.GetFileName).OfType<string>()
                .Where(d => d.StartsWith(run, StringComparison.OrdinalIgnoreCase)).Order().LastOrDefault()
            : null;
        if (source is null)
        {
            Console.Error.WriteLine($"No run matching '{run}' in {real.RunsDir}.");
            return 1;
        }
        var data = Path.Combine(Path.GetTempPath(), "bobar-shots-data", Guid.NewGuid().ToString("N")[..8]);
        CopyDir(Path.Combine(real.RunsDir, source), Path.Combine(data, "runs", source));
        var paths = new BobarPaths(real.ConfigDir, data);

        AppBuilder.Configure<App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Directory.CreateDirectory(outDir);
        try
        {
            var vm = new MainViewModel(paths);
            vm.SelectRun(source);
            vm.Refresh();
            var window = new MainWindow { DataContext = vm, Width = size[0], Height = size[1] };
            window.Show();
            Settle();
            if (expand)
            {
                Expand(vm);
                Settle();
            }
            foreach (var z in zooms)
            {
                vm.Zoom = z;
                Settle();
                var file = Path.Combine(outDir, $"{source}@{z.ToString(CultureInfo.InvariantCulture)}.png");
                if (window.CaptureRenderedFrame() is not { } frame)
                {
                    Console.Error.WriteLine("Nothing was rendered.");
                    return 1;
                }
                frame.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Console.WriteLine(file);
            }
            window.Close();
            vm.Dispose();
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return 0;
    }

    /// <summary>Lets bindings, layout and rendering catch up.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Expand(MainViewModel vm)
    {
        if (vm.Detail is not { } d) return;
        foreach (var round in d.Table.Rounds)
        {
            round.IsExpanded = round.CanExpand;
            foreach (var f in round.Findings) f.IsExpanded = f.CanExpand;
        }
        foreach (var row in d.Hindsight.AcceptedWrongly.Concat(d.Hindsight.RejectedWrongly)) row.IsExpanded = row.CanExpand;
        // A discussion: open the first turn on its activity.
        if (d.Discuss is { } disc && disc.Rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.CanOpen) is { } cell)
        {
            disc.Open(cell);
            disc.ShowActivityViewCommand.Execute(null);
        }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
