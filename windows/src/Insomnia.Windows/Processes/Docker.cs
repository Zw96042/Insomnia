using Insomnia.Core.Logging;
using Insomnia.Core.Platform;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Docker Desktop for the Docker rule: which of its processes to suspend, and
/// whether any container is running. The WSL 2 VM belongs to the system and
/// is not one of Desktop's processes.
/// </summary>
public sealed class Docker : IDocker
{
    private const string DesktopImage = "Docker Desktop.exe";
    private const string BackendPrefix = "com.docker.";

    /// <summary>
    /// Docker Desktop's own engine endpoint. The probe names it with
    /// <c>--host</c>, which the CLI ranks above <c>DOCKER_HOST</c> and the
    /// current context, so an ambient remote daemon cannot answer for the
    /// local Desktop that is about to be suspended. When Desktop's engine is
    /// not up the pipe is missing and the probe fails, leaving Docker alone.
    /// </summary>
    private const string EnginePipe = "docker_engine";

    private readonly IProcessCatalog _catalog;
    private readonly ICommandRunner _runner;

    public Docker(IProcessCatalog catalog, ICommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runner);
        _catalog = catalog;
        _runner = runner;
    }

    public IReadOnlyList<ProcessInfo> DesktopProcesses() =>
        _catalog.Snapshot()
            .Where(p => p.IsCurrentUser
                && (string.Equals(p.Image, DesktopImage, StringComparison.OrdinalIgnoreCase)
                    || p.Image.StartsWith(BackendPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public async Task<DockerProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = DockerExecutable();
        string[] arguments = ["--host", $"npipe:////./pipe/{EnginePipe}", "ps", "-q"];
        var result = await _runner.RunAsync(executable, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            Log.Info("docker probe: cancelled; failed");
            return DockerProbeResult.Failed;
        }
        if (result.TimedOut)
        {
            Log.Warn($"docker probe: no answer within {timeout.TotalSeconds:0.#} s; failed");
            return DockerProbeResult.Failed;
        }
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            Log.Warn($"docker probe: {executable} exited with {result.ExitCode}{(detail.Length == 0 ? string.Empty : ": " + detail)}; failed");
            return DockerProbeResult.Failed;
        }
        if (result.StandardOutput.Trim().Length == 0)
        {
            Log.Info("docker probe: no running container");
            return DockerProbeResult.NoContainers;
        }
        var count = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        Log.Info($"docker probe: {count} running container(s)");
        return DockerProbeResult.Busy;
    }

    /// <summary>The CLI Docker Desktop installs, else whatever <c>docker</c> resolves to on PATH.</summary>
    private static string DockerExecutable()
    {
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
        return File.Exists(bundled) ? bundled : "docker";
    }
}
