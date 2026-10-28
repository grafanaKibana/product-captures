using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;

namespace ProductCaptures;

public static class Processes
{
    static readonly ConcurrentDictionary<Process, int> Groups = new();
    [DllImport("libc", SetLastError = true)] static extern int setpgid(int pid, int pgid);
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int signal);

    public static async Task<int> ExecAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || OperatingSystem.IsWindows() || setpgid(0, 0) < 0) throw new InvalidOperationException("Could not create owned process group");
            using var child = Process.Start(StartInfo(args[0], args.Skip(1), Directory.GetCurrentDirectory(), false)) ?? throw new InvalidOperationException($"Could not start {args[0]}");
            await child.WaitForExitAsync(); return child.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args, string cwd, bool redirect)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = redirect, RedirectStandardError = redirect, RedirectStandardInput = redirect };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static Process Start(string executable, IEnumerable<string> args, string cwd, bool redirect = true)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows()) info = StartInfo(executable, args, cwd, redirect);
        else
        {
            var host = Environment.ProcessPath is { } current && Path.GetFileNameWithoutExtension(current) == "dotnet" ? current : "dotnet";
            info = StartInfo(host, new[] { typeof(Program).Assembly.Location, "__exec", executable }.Concat(args), cwd, redirect);
        }
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}");
        if (!OperatingSystem.IsWindows()) Groups[process] = process.Id;
        return process;
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> args, string cwd, int timeoutMs = 600000, string? input = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(timeoutMs);
        using var process = Start(executable, args, cwd);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).WaitAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output.WaitAsync(timeout.Token); var stderr = await error.WaitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(text => text.Length > 0))}");
            return stdout;
        }
        finally { Stop(process); }
    }

    public static Task<string> ShellAsync(BuildCommand build, CancellationToken cancellationToken = default) => OperatingSystem.IsWindows()
        ? RunAsync("cmd.exe", ["/d", "/s", "/c", build.Command], build.Cwd, build.TimeoutMs, cancellationToken: cancellationToken)
        : RunAsync("/bin/sh", ["-c", build.Command], build.Cwd, build.TimeoutMs, cancellationToken: cancellationToken);
    public static void Stop(Process process)
    {
        var grouped = Groups.TryRemove(process, out var group);
        if (grouped) _ = kill(-group, 9);
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        // Catch the narrow startup race where setpgid ran after the first group signal.
        if (grouped) _ = kill(-group, 9);
    }
    public static string? Commit(string root, CancellationToken cancellationToken = default)
    {
        try { return RunAsync("git", ["rev-parse", "HEAD"], root, 10000, cancellationToken: cancellationToken).GetAwaiter().GetResult().Trim(); } catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }
}

public sealed class CaptureServer : IAsyncDisposable
{
    readonly HttpListener? listener;
    readonly Process? process;
    readonly CancellationTokenSource stopped = new();
    readonly Task? serving;
    public string Url { get; }
    CaptureServer(string url, HttpListener? listener = null, Process? process = null, string? directory = null)
    {
        Url = url; this.listener = listener; this.process = process;
        if (listener is not null) serving = ServeAsync(directory!);
    }
    public static async Task<CaptureServer> StartAsync(Config config, string? directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var server = config.Server;
        if (directory is not null || server?.Directory is not null)
        {
            directory ??= Paths.Within(config.Root, server!.Directory!, true);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var url = $"http://127.0.0.1:{server?.Port ?? 4180}/";
            var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
            return new CaptureServer(url, listener, directory: directory);
        }
        if (server?.Command is null) throw new ArgumentException("Web capture requires server or --url");
        var endpoint = new Uri(server.Url!);
        foreach (var address in Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback })
        {
            var probe = new TcpListener(address, endpoint.Port);
            try { probe.Start(); }
            catch (SocketException) { throw new InvalidOperationException($"Server port {endpoint.Port} is already occupied; use --url for an existing server"); }
            finally { probe.Stop(); }
        }
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(server.TimeoutMs);
        var startupToken = startup.Token;
        startupToken.ThrowIfCancellationRequested();
        var process = Processes.Start(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? ["/d", "/s", "/c", server.Command] : ["-c", server.Command], config.Root);
        var stdout = new ConcurrentQueue<string>(); var stderr = new ConcurrentQueue<string>();
        async Task DrainAsync(StreamReader reader, ConcurrentQueue<string> chunks)
        {
            try
            {
                var buffer = new char[4096]; int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), startupToken)) > 0) chunks.Enqueue(new string(buffer, 0, count));
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        var output = DrainAsync(process.StandardOutput, stdout); var errors = DrainAsync(process.StandardError, stderr);
        process.StandardInput.Close();
        var result = new CaptureServer(server.Url!, process: process);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                startupToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"Server exited {process.ExitCode}");
                try { using var response = await client.GetAsync(result.Url, startupToken); if (response.IsSuccessStatusCode) return result; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!startup.IsCancellationRequested) { }
                await Task.Delay(200, startupToken);
            }
        }
        catch (Exception error)
        {
            Processes.Stop(process);
            try { await Task.WhenAll(output, errors).WaitAsync(startupToken); } catch (OperationCanceledException) { }
            await result.DisposeAsync();
            var diagnostics = string.Join("\n", new[] { string.Concat(stdout).Trim(), string.Concat(stderr).Trim() }.Where(text => text.Length > 0));
            var message = error.Message + (diagnostics.Length > 0 ? "\n" + diagnostics : "");
            if (error is OperationCanceledException canceled) throw new OperationCanceledException(message, canceled, canceled.CancellationToken);
            throw new InvalidOperationException(message, error);
        }
    }

    async Task ServeAsync(string directory)
    {
        while (!stopped.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener!.GetContextAsync().WaitAsync(stopped.Token); } catch (Exception) when (stopped.IsCancellationRequested) { break; }
            _ = RespondAsync(context, directory);
        }
    }
    static async Task RespondAsync(HttpListenerContext context, string directory)
    {
        try
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD")) { context.Response.StatusCode = 405; return; }
            var path = Paths.Within(directory, Uri.UnescapeDataString(context.Request.Url!.AbsolutePath).TrimStart('/'), true);
            if (Directory.Exists(path)) path = Paths.Within(directory, Path.Combine(path, "index.html"));
            if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = Path.GetExtension(path).ToLowerInvariant() switch { ".html" => "text/html", ".css" => "text/css", ".js" or ".mjs" => "text/javascript", ".json" => "application/json", ".svg" => "image/svg+xml", ".png" => "image/png", ".woff2" => "font/woff2", _ => "application/octet-stream" };
            context.Response.ContentLength64 = new FileInfo(path).Length;
            if (context.Request.HttpMethod == "GET") { await using var stream = File.OpenRead(path); await stream.CopyToAsync(context.Response.OutputStream); }
        }
        catch (ArgumentException) { context.Response.StatusCode = 403; }
        catch (Exception) { context.Response.StatusCode = 500; }
        finally { context.Response.Close(); }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); listener?.Close(); if (process is not null) { Processes.Stop(process); process.Dispose(); }
        if (serving is not null) await serving; stopped.Dispose();
    }
}
