using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> with the project-specific
/// GitHub release source. Use a single shared instance from app startup —
/// don't construct per-call. All operations are async and safe to call from
/// the UI thread (Velopack handles thread-pool internally).
/// </summary>
public sealed class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/RafalZG/claude-sessions-sidekick";

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        _manager = new UpdateManager(new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>True only when the running build was installed via Velopack.
    /// Standalone exe runs (e.g. dev box <c>dotnet run</c>) return false and
    /// every update operation is a no-op — guard checks accordingly.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>
    /// SemVer of the currently-installed build as Velopack sees it (e.g.
    /// <c>1.0.0-rc2</c>). Empty for non-installed (dev) runs — the caller is
    /// expected to fall back to assembly version in that case.
    /// </summary>
    public string InstalledVersion =>
        _manager.IsInstalled ? _manager.CurrentVersion?.ToString() ?? "" : "";

    /// <summary>Returns null when no newer release is available, the
    /// <see cref="UpdateInfo"/> otherwise. Network failures are logged and
    /// surface as null (caller treats "couldn't check" as "no update").</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!IsInstalled)
        {
            return null;
        }
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch (System.Exception ex)
        {
            AppLogger.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the update package locally. Progress is reported
    /// 0..100. Caller is responsible for awaiting completion before calling
    /// <see cref="ApplyAndRestart"/>.</summary>
    public async Task DownloadUpdatesAsync(UpdateInfo update, System.Action<int>? progressPercent = null)
    {
        await _manager.DownloadUpdatesAsync(update, p => progressPercent?.Invoke(p));
    }

    /// <summary>Swaps the on-disk binary and relaunches. The current process
    /// exits as a side-effect — anything you need to flush (e.g. settings)
    /// must be persisted BEFORE calling this.</summary>
    public void ApplyAndRestart(UpdateInfo update)
    {
        _manager.ApplyUpdatesAndRestart(update);
    }
}
