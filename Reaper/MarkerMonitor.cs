using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReaperControl.Reaper;

/// <summary>
/// Polls REAPER's marker list and cursor position in the background while marker buttons are shown,
/// and tells Macro Deck to re-read those buttons when a marker or the current marker changes.
/// </summary>
public sealed class MarkerMonitor(
    ReaperConnection connection,
    IPluginCatalogNotifier notifier,
    MarkerFilterState filters,
    ILogger<MarkerMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(75);

    // Markers count as "current" from their position on; a small tolerance for float positions.
    private const double PositionTolerance = 0.0005;

    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private long _lastWanted;
    private MarkerSnapshot? _snapshot;
    private string? _signature;
    private bool _reachable = true;

    // Color REAPER draws markers without a color of their own in (from the theme), published by
    // ReaperControl_ThemeColors.lua. That script runs once when it is missing - after REAPER or the
    // plugin started - not continuously: themes rarely change.
    public const string ThemeColorsScript = "ReaperControl_ThemeColors.lua";
    private const string FallbackMarkerColor = "#a00000"; // REAPER's default marker red
    private const string FallbackRegionColor = "#508585"; // REAPER's default region color
    private static readonly TimeSpan ThemeScriptRetry = TimeSpan.FromSeconds(30);
    private readonly ExtStateValue _themeMarkerColor = new("ReaperControl", "ThemeMarkerColor");
    private readonly ExtStateValue _themeRegionColor = new("ReaperControl", "ThemeRegionColor");
    private string? _themeColor;
    private string? _themeRegion;
    private long _themeScriptStarted = -ThemeScriptRetry.Ticks;
    private bool _warnedThemeScript;

    /// <summary>"#rrggbb" for markers without their own color: the theme's, else REAPER's default red.</summary>
    public string DefaultMarkerColor => Volatile.Read(ref _themeColor) ?? FallbackMarkerColor;

    /// <summary>"#rrggbb" for regions without their own color: the theme's, else REAPER's default.</summary>
    public string DefaultRegionColor => Volatile.Read(ref _themeRegion) ?? FallbackRegionColor;

    /// <summary>The color a marker or region is drawn in: its own, else the theme default.</summary>
    public string ColorOf(ReaperMarker m) =>
        (m.Color is { } c && c != 0 ? $"#{c & 0xFFFFFF:x6}" : null)
        ?? (m.IsRegion ? DefaultRegionColor : DefaultMarkerColor);

    /// <summary>The last known markers, and keep polling them. Null until the first answer.</summary>
    public MarkerSnapshot? Get()
    {
        var first = Interlocked.Exchange(ref _lastWanted, Environment.TickCount64) == 0;
        if (first)
        {
            RequestRefresh();
        }

        return Volatile.Read(ref _snapshot);
    }

    public void RequestRefresh()
    {
        try
        {
            _wakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
            // A refresh is already pending.
        }
    }

    /// <summary>Makes Macro Deck re-read this plugin's buttons now (e.g. after the filter changed).</summary>
    public void NotifyButtonsChanged()
    {
        notifier.CatalogChanged("actions", reason: "markers");
        // The filter badge of next/previous buttons is an icon, read separately from states.
        connection.InvalidateIcons(ReaperControl.Actions.NextPreviousMarkerAction.ActionId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeUp.WaitAsync(PollInterval, stoppingToken);
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling REAPER markers failed");
            }
        }
    }

    /// <summary>Reads the theme marker color from the reply; starts the script once if it is missing.</summary>
    private async Task UpdateThemeColorAsync(ReaperWebClient client, string reply, CancellationToken cancellationToken)
    {
        var states = ReaperResponseParser.ParseStates(reply, [_themeMarkerColor, _themeRegionColor]);
        var raw = states.GetValueOrDefault(_themeMarkerColor.Key, "");
        if (raw.Length == 7 && raw[0] == '#')
        {
            Volatile.Write(ref _themeColor, raw.ToLowerInvariant());
            var region = states.GetValueOrDefault(_themeRegionColor.Key, "");
            if (region.Length == 7 && region[0] == '#')
            {
                Volatile.Write(ref _themeRegion, region.ToLowerInvariant());
            }

            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - _themeScriptStarted < ThemeScriptRetry.Ticks)
        {
            return;
        }

        _themeScriptStarted = now;
        var commandId = connection.Catalog.FindScriptCommandId(ThemeColorsScript, ReaperScriptFiles.InstallFolder);
        if (commandId is null)
        {
            if (!_warnedThemeScript)
            {
                _warnedThemeScript = true;
                logger.LogWarning("{Script} is not in REAPER's action list - markers without a color use {Color}. " +
                                  "Install the scripts and run ReaperControl_Setup.lua once.",
                    ThemeColorsScript, FallbackMarkerColor);
            }

            return;
        }

        await client.SendEncodedAsync([Uri.EscapeDataString(commandId)], cancellationToken);
    }

    /// <summary>"M3" / "R3", as REAPER labels markers and regions.</summary>
    public static string Key(ReaperMarker? m) => m is null ? "-" : (m.IsRegion ? "R" : "M") + m.Id;

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var client = connection.Client;
        if (client is null || Environment.TickCount64 - Interlocked.Read(ref _lastWanted) > WatchTimeout.TotalMilliseconds)
        {
            return;
        }

        string reply;
        try
        {
            reply = await client.SendAsync(["MARKER", "REGION", "TRANSPORT", _themeMarkerColor.Request, _themeRegionColor.Request], cancellationToken);
            if (!_reachable)
            {
                _reachable = true;
                logger.LogInformation("REAPER is reachable again (markers)");
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            if (_reachable)
            {
                _reachable = false;
                logger.LogWarning("REAPER does not answer the marker request ({Message})", ex.Message);
            }

            return;
        }

        await UpdateThemeColorAsync(client, reply, cancellationToken);

        var items = ReaperResponseParser.ParseMarkers(reply)
            .OrderBy(m => m.Start)
            .ThenBy(m => m.IsRegion) // a marker before a region starting at the same place
            .ThenBy(m => m.Id)
            .ToList();
        var position = ReaperResponseParser.ParseTransport(reply)?.PositionSeconds ?? 0;
        var snapshot = new MarkerSnapshot(items, position);
        Volatile.Write(ref _snapshot, snapshot);

        // Only what buttons show: names, ids, colors, what is current, and next/previous for every view
        // a button can follow - not every cursor move.
        var views = new[] { MarkerView.Plain }.Concat(filters.Groups.Select(g => g.View));
        var signature = string.Join('\u0001', items.Select(m =>
                            $"{(m.IsRegion ? 'R' : 'M')}{m.Id}\u0002{m.Name}\u0002{m.Color}\u0002{snapshot.IsCurrent(m)}"))
                        + "\u0003" + string.Join(",", views.Select(v =>
                            $"{Key(snapshot.Previous(v))}/{Key(snapshot.Next(v))}"))
                        + "\u0003" + DefaultMarkerColor + DefaultRegionColor;
        var markers = items;
        if (signature != _signature)
        {
            logger.LogInformation("REAPER markers: {Count} markers/regions ({Ids}), current marker {Current}",
                markers.Count, string.Join(", ", markers.Select(Key)), snapshot.CurrentMarkerId?.ToString() ?? "none");
            if (markers.Count == 0)
            {
                // Shows what REAPER actually answered, in case the format is not what the parser expects.
                logger.LogInformation("REAPER answer to MARKER;TRANSPORT: {Reply}",
                    reply.Length > 500 ? reply[..500] : reply);
            }

            _signature = signature;
            NotifyButtonsChanged();
        }
    }

    public override void Dispose()
    {
        _wakeUp.Dispose();
        base.Dispose();
    }
}
