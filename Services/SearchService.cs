// WinPlasma.Spotlight — Services/SearchService.cs
// Searches apps (Start Menu), files (Windows Search index), and Settings.

using System.IO;

namespace WinPlasma.Spotlight.Services;

/// <summary>
/// Fast search across apps, files, and settings.
/// Uses filesystem enumeration for apps (Start Menu folders) for speed.
/// File search uses Windows Search API when available, falls back to filesystem.
/// </summary>
public sealed class SearchService
{
    // Pre-loaded list of Start Menu apps (loaded on plugin start)
    private List<SearchResult> _appCache = [];

    public async Task InitializeAsync()
    {
        // Pre-warm the app cache
        _appCache = await Task.Run(LoadAppsFromStartMenu);
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var results = new List<SearchResult>();
        var queryLower = query.ToLowerInvariant().Trim();

        // 1. Apps (from cache — instant)
        var appResults = _appCache
            .Where(a => a.Title.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Title.StartsWith(queryLower, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();
        results.AddRange(appResults);

        // 2. Files (Windows Search index query)
        if (results.Count < 10)
        {
            var fileResults = await SearchFilesAsync(queryLower, ct);
            results.AddRange(fileResults.Take(10 - results.Count));
        }

        // 3. Settings pages
        var settingsResults = SearchSettings(queryLower);
        results.AddRange(settingsResults.Take(3));

        return results;
    }

    // ── App search ────────────────────────────────────────────────────────────

    private static List<SearchResult> LoadAppsFromStartMenu()
    {
        var results = new List<SearchResult>();
        var startMenuFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var folder in startMenuFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                results.Add(new SearchResult
                {
                    Title = name,
                    Subtitle = "Application",
                    ResultType = SearchResultType.App,
                    ActionPath = file,
                    Icon = "\uE737" // App icon glyph
                });
            }
        }

        return results.OrderBy(r => r.Title).ToList();
    }

    // ── File search ───────────────────────────────────────────────────────────

    private static async Task<List<SearchResult>> SearchFilesAsync(string query, CancellationToken ct)
    {
        // Use Windows Search OLE DB provider for indexed search
        // This is extremely fast — searches the Windows Search index, not the filesystem directly
        var results = new List<SearchResult>();
        try
        {
            // OLE DB connection to Windows Search
            var connectionString = "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";
            var sql = $"""
                SELECT System.ItemName, System.ItemPathDisplay, System.ItemTypeText
                FROM SYSTEMINDEX
                WHERE CONTAINS(System.ItemName, '"{query}*"')
                AND scope='file:'
                ORDER BY System.DateModified DESC
            """;

            // For Phase 1, run a simple filesystem search as fallback
            // Full OLE DB search integration is a Phase 2 enhancement
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(userProfile, $"*{query}*",
                    new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3,
                        IgnoreInaccessible = true }))
                {
                    if (ct.IsCancellationRequested) break;
                    results.Add(new SearchResult
                    {
                        Title = Path.GetFileName(file),
                        Subtitle = Path.GetDirectoryName(file) ?? string.Empty,
                        ResultType = SearchResultType.File,
                        ActionPath = file,
                        Icon = "\uE8A5"
                    });
                    if (results.Count >= 8) break;
                }
            }, ct);
        }
        catch { /* File search failure is non-fatal */ }

        return results;
    }

    // ── Settings search ───────────────────────────────────────────────────────

    private static readonly List<(string name, string uri)> SettingsPages =
    [
        ("Display Settings",    "ms-settings:display"),
        ("Sound Settings",      "ms-settings:sound"),
        ("Bluetooth",           "ms-settings:bluetooth"),
        ("Wi-Fi",               "ms-settings:network-wifi"),
        ("Apps",                "ms-settings:appsfeatures"),
        ("Windows Update",      "ms-settings:windowsupdate"),
        ("Privacy",             "ms-settings:privacy"),
        ("Personalization",     "ms-settings:personalization"),
        ("Accounts",            "ms-settings:accounts"),
        ("Accessibility",       "ms-settings:easeofaccess"),
        ("Date & Time",         "ms-settings:dateandtime"),
        ("Language",            "ms-settings:regionlanguage"),
        ("Startup Apps",        "ms-settings:startupapps"),
        ("Task Manager",        "taskmgr"),
        ("Control Panel",       "control"),
    ];

    private static List<SearchResult> SearchSettings(string query)
    {
        return SettingsPages
            .Where(s => s.name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new SearchResult
            {
                Title = s.name,
                Subtitle = "Settings",
                ResultType = SearchResultType.Settings,
                ActionPath = s.uri,
                Icon = "\uE713"
            })
            .ToList();
    }

    /// <summary>Launch the action for a result — open file, app, or settings URI.</summary>
    public static void Launch(SearchResult result)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.ActionPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchService] Launch failed: {ex.Message}");
        }
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public enum SearchResultType { App, File, Settings, Web }

public sealed class SearchResult
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Icon { get; init; } = "\uE8A5";
    public SearchResultType ResultType { get; init; }
    public string ActionPath { get; init; } = string.Empty;
}
