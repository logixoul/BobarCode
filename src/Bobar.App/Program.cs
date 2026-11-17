using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Threading;

namespace Bobar.App;

public static class Program
{
    private const string MutexName = @"Local\BobarCode.App.SingleInstance";
    private static string PipeName => "BobarCode.App." + Environment.UserName;

    /// <summary>Arguments that arrive from a second launch; handled on the UI thread.</summary>
    public static event Action<string[]>? ArgsReceived;

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Single instance: pass our args to the running one, which comes to the front.
            return SendToFirstInstance(args) ? 0 : 1;
        }
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => PipeServerLoop(cts.Token));
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            cts.Cancel();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>`--run &lt;id&gt;` from the command line.</summary>
    public static string? RunArg(string[] args)
    {
        var i = Array.IndexOf(args, "--run");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool SendToFirstInstance(string[] args)
    {
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1); // ASFW_ANY: let the first instance take focus
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(1000);
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
                client.Write(bytes);
                client.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(300); // the first instance may still be starting
            }
        }
        return false;
    }

    private static async Task PipeServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(ct);
                var args = text.Length == 0 ? [] : text.Split('\n');
                Dispatcher.UIThread.Post(() => ArgsReceived?.Invoke(args));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
