using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "Go to next / previous marker": jumps to the marker (or region) right (next) or left (previous) of
/// the play/edit cursor. The label shows the direction and its name, and follows the cursor.
/// </summary>
/// <remarks>
/// The button's filter group always decides the kind (markers / regions / both, set by "Marker type").
/// "Follow marker filter" decides only about the group's name filter ("Marker filter").
/// </remarks>
public sealed class NextPreviousMarkerAction(
    ReaperConnection connection,
    MarkerMonitor markers,
    MarkerFilterState filters,
    ILogger<NextPreviousMarkerAction> logger)
    : IActionDefinition, IStateProviderActionDefinition, IIconProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "go-to-next-previous-marker";

    private const string DirectionParameter = "direction";
    private const string FollowFilterParameter = "followFilter";   // old toggle, still read
    private const string FilterModeParameter = "filterMode";
    private const string UseColorParameter = "useMarkerColor";

    // Direction value for the switch button; state ids persisted in profiles - never rename.
    private const string ToggleDirection = "toggle-follow-filter";

    public string Id => ActionId;

    public LocalizedText Name => "Go to next / previous marker";

    public LocalizedText Description =>
        "Jumps to the next or previous marker and shows its name. Optionally only markers matching the marker filter.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(DirectionParameter,
            [
                new ActionParameterOption { Value = "previous", Label = "Previous marker (left of the cursor)" },
                new ActionParameterOption { Value = "next", Label = "Next marker (right of the cursor)" },
                new ActionParameterOption
                {
                    Value = ToggleDirection,
                    Label = "Switch: next/previous buttons follow the name filter on/off"
                }
            ],
            "Direction", defaultValue: "next", required: true),
        ActionParameter.Choice(FilterModeParameter,
            [
                new ActionParameterOption { Value = "toggle", Label = "As the switch button says" },
                new ActionParameterOption { Value = "always", Label = "Always follow the name filter" },
                new ActionParameterOption { Value = "never", Label = "Never (ignore the name filter)" }
            ],
            "Follow marker filter",
            "Whether a press skips entries that do not match the group's name filter. Markers / regions / both always come from the group.",
            defaultValue: "toggle").OnlyWhen(DirectionParameter, "next", "previous"),
        // Default A: buttons from before filter groups followed the one filter, now group A.
        MarkerGroupParameter.ForFilters("The filter group this button follows, and whose switch the switch button toggles."),
        ActionParameter.Toggle(UseColorParameter, "Use marker color",
            "Button in the color of the marker or region it jumps to (without a color of its own: REAPER's theme color).",
            defaultValue: true)
            .OnlyWhen(DirectionParameter, "next", "previous")
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with a state (Macro Deck offers "use provided states" only when the action is added).
        // The switch provides no states: it often shares a button with "previous"/"next" (Long Press),
        // and a button can follow only one state provider - the marker label must win.
        if (IsToggle(parameters))
        {
            return Task.FromResult<ActionStateSnapshot?>(null);
        }

        var next = IsNext(parameters);
        var heading = next ? "Go to next" : "Go to previous";
        var snapshot = connection.Client is null ? null : markers.Get();
        var marker = snapshot is null ? null : Target(snapshot, next, parameters);

        string label;
        string? color = null;
        if (marker is null)
        {
            label = $"{heading}\n–";
        }
        else
        {
            var markerText = string.IsNullOrWhiteSpace(marker.Name) ? MarkerMonitor.Key(marker) : marker.Name;
            label = $"{heading}\n{markerText}";
            // Without a color of its own: the color REAPER draws it in (theme, else red).
            color = UsesMarkerColor(parameters) ? markers.ColorOf(marker) : null;
        }

        var background = color ?? "#2d3748";
        var stateId = TextStateIds.For(next ? "next" : "previous", $"{label}\u0001{color}");
        var state = new ActionStateDefinition(stateId, label)
        {
            DefaultAppearance = new ActionStateAppearance
            {
                Label = label, BackgroundColor = background, LabelColor = MarkerColors.ReadableTextOn(background)
            }
        };

        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot([state], stateId));
    }

    public TimeSpan IconPollInterval => TimeSpan.FromSeconds(1);

    /// <summary>
    /// A small filter symbol in the top right corner (Assets/Icons/filter-badge.png: transparent except
    /// the symbol) while this button skips markers that do not match the active marker filter.
    /// </summary>
    public Task<ActionIconSnapshot?> GetActionIconAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        if (IsToggle(parameters))
        {
            return Task.FromResult<ActionIconSnapshot?>(null);
        }

        // The badge: a name filter is active for this button.
        var filtered = ViewOf(parameters).Filter is not null;
        return Task.FromResult(filtered
            ? IconLibrary.Snapshot("filter-badge", dimmed: false)
            : new ActionIconSnapshot { NoIcon = true });
    }

    public Task<ActionIconContent?> GetActionIconContentAsync(
        IReadOnlyDictionary<string, object?> parameters, string version, CancellationToken cancellationToken) =>
        Task.FromResult(IconLibrary.Content(version));

    public IActionExecutor CreateExecutor() => new Executor(this, connection, markers, logger);

    private ReaperMarker? Target<T>(MarkerSnapshot snapshot, bool next, IReadOnlyDictionary<string, T> parameters)
    {
        var view = ViewOf(parameters);
        return next ? snapshot.Next(view) : snapshot.Previous(view);
    }

    private MarkerFilterGroup GroupOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        filters.Group(MarkerGroupParameter.Read(parameters, "A")) ?? filters.A;

    /// <summary>The group's kind, and its name filter while following it.</summary>
    private MarkerView ViewOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var group = GroupOf(parameters);
        return new MarkerView(group.Kind, FollowsFilter(parameters) ? group.Current : null);
    }

    private static bool IsNext<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, DirectionParameter) != "previous";


    private static bool IsToggle<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, DirectionParameter) == ToggleDirection;

    private bool FollowsFilter<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, FilterModeParameter) switch
        {
            "always" => true,
            "never" => false,
            "toggle" => GroupOf(parameters).NavigationFollowsFilter,
            // Buttons from before this setting: the old on/off switch.
            _ => string.Equals(Values.Get(parameters, FollowFilterParameter), "true", StringComparison.OrdinalIgnoreCase)
        };

    // Default on, also for buttons configured before the setting existed.
    private static bool UsesMarkerColor<T>(IReadOnlyDictionary<string, T> parameters) =>
        !string.Equals(Values.Get(parameters, UseColorParameter), "false", StringComparison.OrdinalIgnoreCase);

    private sealed class Executor(
        NextPreviousMarkerAction action,
        ReaperConnection connection,
        MarkerMonitor markers,
        ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            if (IsToggle(context.Parameters))
            {
                action.GroupOf(context.Parameters).ToggleNavigationFollowsFilter();
                markers.NotifyButtonsChanged(); // next/previous buttons show other markers now
                return ActionResult.Success();
            }

            var client = connection.Client;
            if (client is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotConfigured, "REAPER connection is not set up yet.");
            }

            // The marker the label shows, so the press goes where the button says.
            var snapshot = markers.Get();
            var marker = snapshot is null ? null : action.Target(snapshot, IsNext(context.Parameters), context.Parameters);
            if (marker is null)
            {
                return ActionResult.Success(); // no marker in that direction
            }

            try
            {
                await client.SendAsync([$"SET/POS_STR/{MarkerButtonAction.PositionOf(marker)}"], context.CancellationToken);
                markers.RequestRefresh();
                return ActionResult.Success();
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return ActionResult.Failed(ActionErrorCodes.Timeout, "REAPER did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Jumping to {Marker} failed", MarkerMonitor.Key(marker));
                return ActionResult.Failed(ActionErrorCodes.NotConnected,
                    "REAPER is not reachable. Is it running with the web interface enabled?");
            }
        }
    }
}
