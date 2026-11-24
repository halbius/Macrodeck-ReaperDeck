using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>Marker colors from REAPER's web interface (0x01rrggbb; 0 = no custom color).</summary>
internal static class MarkerColors
{
    /// <summary>"#rrggbb", or null when the marker has no custom color.</summary>
    public static string? ToHex(int? color) =>
        color is { } c && c != 0 ? $"#{c & 0xFFFFFF:x6}" : null;

    /// <summary>The color at 40 % brightness, for markers that are not the current one.</summary>
    public static string Dim(string hex)
    {
        var (r, g, b) = Parse(hex);
        return $"#{(int)(r * 0.4):x2}{(int)(g * 0.4):x2}{(int)(b * 0.4):x2}";
    }

    /// <summary>Black or white, whichever reads better on <paramref name="hex" />.</summary>
    public static string ReadableTextOn(string hex)
    {
        var (r, g, b) = Parse(hex);
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        return luminance > 0.6 ? "#000000" : "#ffffff";
    }

    private static (int R, int G, int B) Parse(string hex)
    {
        var value = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }
}

/// <summary>Stable, kebab-case state ids derived from text (state ids are persisted in profiles).</summary>
internal static class TextStateIds
{
    /// <summary><paramref name="prefix" /> + "-" + FNV-1a hash of <paramref name="text" />.</summary>
    public static string For(string prefix, string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return $"{prefix}-{hash:x8}";
    }
}

/// <summary>The "filter group" setting shared by the marker actions (groups A-D, see MarkerFilterState).</summary>
internal static class MarkerGroupParameter
{
    public const string Name = "filterGroup";
    public const string None = "none";

    /// <summary>A-D, for buttons that set a filter (default A).</summary>
    public static ActionParameter ForFilters(string description) =>
        ActionParameter.Choice(Name,
            MarkerFilterState.GroupNames.Select(g => new ActionParameterOption { Value = g, Label = $"Group {g}" })
                .ToList(),
            "Filter group", description, defaultValue: "A", required: true);

    /// <summary>None or A-D, for buttons that follow a group (default none).</summary>
    public static ActionParameter ForFollowers(string description) =>
        ActionParameter.Choice(Name,
            [
                new ActionParameterOption { Value = None, Label = "None (markers, no filter)" },
                .. MarkerFilterState.GroupNames.Select(g =>
                    new ActionParameterOption { Value = g, Label = $"Group {g}" })
            ],
            "Follow filter group", description, defaultValue: None);

    /// <summary>The group a button names; <paramref name="fallback" /> when the setting is missing (older buttons).</summary>
    public static string? Read<T>(IReadOnlyDictionary<string, T> parameters, string? fallback) =>
        Values.Get(parameters, Name) switch
        {
            null => fallback,
            None => null,
            var g => g
        };

    /// <summary>"Markers", "Regions", "Markers + Regions".</summary>
    public static string KindName(MarkerKind kind) => kind switch
    {
        MarkerKind.Regions => "Regions",
        MarkerKind.Both => "Markers + Regions",
        _ => "Markers"
    };
}

/// <summary>
/// "Go to marker": jumps to a marker or region and carries its name and "M3" / "R3" as label; lit while
/// it is current (a marker: the last one at or before the cursor; a region: while the cursor is inside).
/// </summary>
/// <remarks>
/// Without a filter group: always the marker with this ID. With a group: its kind (markers / regions /
/// both, set by "Marker type") and its name filter (set by "Marker filter") decide what the button
/// shows. One kind and no name filter: the marker/region with this ID. "Both" or a name filter: number
/// n is the n-th matching entry in time order - meant for a folder full of these, numbered 1, 2, 3, ...
/// The label works through button states: every marker gets its own state ids, whose default label is
/// its name. Macro Deck adopts new states with their default appearance, also at runtime, so the label
/// follows when the filter or the markers change.
/// </remarks>
public sealed class MarkerButtonAction(
    ReaperConnection connection,
    MarkerMonitor markers,
    MarkerFilterState filters,
    ILogger logger)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "go-to-marker";

    private const string NumberParameter = "number";
    private const string FollowFilterParameter = "followFilter"; // before filter groups: true = group A
    private const string UseColorParameter = "useMarkerColor";
    private const string OnPressParameter = "onPress";
    private const string SetModeParameter = "setMode";
    private const string RegionModeParameter = "regionMode";
    private const string EmptyStateId = "marker-empty";

    /// <summary>Does the setting on the cursor; see ReaperScripts/ReaperControl_SetMarker.lua.</summary>
    public const string SetScriptFileName = "ReaperControl_SetMarker.lua";

    public string Id => ActionId;

    public LocalizedText Name => "Go to marker";

    public LocalizedText Description =>
        "Jumps to a marker or region and shows its name. Optionally follows a filter group (kind and name filter), " +
        "for a folder of marker buttons numbered 1, 2, 3, ...";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Number(NumberParameter, "Marker ID / slot",
            "Marker ID. Following a group with \"Markers + Regions\" or a name filter: position in that list (1 = first).",
            min: 1, step: 1, defaultValue: 1, required: true),
        MarkerGroupParameter.ForFollowers(
            "Which filter group's kind (markers / regions / both) and name filter this button follows."),
        // Old setting, still read for buttons from before filter groups; never shown.
        ActionParameter.Toggle(FollowFilterParameter, "Follow marker filter")
            .OnlyWhen(MarkerGroupParameter.Name, "legacy"),
        ActionParameter.Toggle(UseColorParameter, "Use marker color",
            "Button in the marker's or region's color: full for the current one, dimmed for the others. " +
            "Without a color of their own: REAPER's theme color (default red).",
            defaultValue: true),
        // Same button, other press (e.g. Long Press): copy the block and switch this to "Set".
        ActionParameter.Choice(OnPressParameter,
            [
                new ActionParameterOption { Value = "goto", Label = "Go to the marker / region" },
                new ActionParameterOption { Value = "set", Label = "Set it to the cursor" }
            ],
            "On press",
            "\"Set\" moves the marker/region this button shows to the cursor, or creates one on an empty button. " +
            "Tip: copy this block to Long Press and switch only this setting.",
            defaultValue: "goto"),
        ActionParameter.Choice(SetModeParameter,
                [
                    new ActionParameterOption { Value = "insert", Label = "Set" },
                    new ActionParameterOption { Value = "edit", Label = "Set and edit the name" }
                ],
                "Set mode", defaultValue: "insert")
            .OnlyWhen(OnPressParameter, "set"),
        ActionParameter.Choice(RegionModeParameter,
                [
                    new ActionParameterOption { Value = "move", Label = "Move: start to the cursor, same length" },
                    new ActionParameterOption { Value = "start", Label = "Start to the cursor, end stays" },
                    new ActionParameterOption { Value = "selection", Label = "To the time selection" }
                ],
                "Regions",
                "How a region is set. A new region (empty button) always comes from the time selection.",
                defaultValue: "move")
            .OnlyWhen(OnPressParameter, "set")
    ];

    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states, even for an unfinished draft: Macro Deck offers "use provided
        // states" only when the action is added, and only if states come back then.
        var snapshot = connection.Client is null ? null : markers.Get();
        var marker = snapshot is null ? null : Resolve(parameters, snapshot);
        if (marker is null)
        {
            return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(
                [
                    new ActionStateDefinition(EmptyStateId, "No marker")
                    {
                        DefaultAppearance = new ActionStateAppearance { Label = "", BackgroundColor = "#2d3748" }
                    }
                ],
                EmptyStateId));
        }

        // "<name>" + line break + "M3" / "R3", or just "M3" for an unnamed marker.
        var key = MarkerMonitor.Key(marker);
        var label = string.IsNullOrWhiteSpace(marker.Name) ? key : $"{marker.Name}\n{key}";
        var color = UsesMarkerColor(parameters) ? markers.ColorOf(marker) : null;

        // The ids carry label and color: a new name or color is a new state, which Macro Deck adopts
        // with its default appearance - that is how label and color follow REAPER.
        // "v3": regions and "M"/"R" labels.
        var baseId = TextStateIds.For("marker", $"v3\u0001{key}\u0001{label}\u0001{color}");
        var onId = baseId + "-on";
        var offId = baseId + "-off";

        IReadOnlyList<ActionStateDefinition> states = color is null
            ?
            [
                new(onId, $"{label} (current)")
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = "#2f855a", LabelColor = "#ffffff"
                    }
                },
                // Explicit colors: a state without a background color gets one filled in by Macro Deck.
                new(offId, label)
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
                    }
                }
            ]
            :
            [
                new(onId, $"{label} (current)")
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = color, LabelColor = MarkerColors.ReadableTextOn(color)
                    }
                },
                new(offId, label)
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label,
                        BackgroundColor = MarkerColors.Dim(color),
                        LabelColor = MarkerColors.ReadableTextOn(MarkerColors.Dim(color))
                    }
                }
            ];

        return Task.FromResult<ActionStateSnapshot?>(
            new ActionStateSnapshot(states, snapshot!.IsCurrent(marker) ? onId : offId));
    }

    public IActionExecutor CreateExecutor() => new Executor(this, connection, markers, logger);

    // Default on: buttons configured before this setting existed have no value and use the color.
    private static bool UsesMarkerColor<T>(IReadOnlyDictionary<string, T> parameters) =>
        !string.Equals(Values.Get(parameters, UseColorParameter), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The group's view, or plain markers without a group.</summary>
    private MarkerView ViewOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var legacy = string.Equals(Values.Get(parameters, FollowFilterParameter), "true",
            StringComparison.OrdinalIgnoreCase) ? "A" : null;
        return filters.Group(MarkerGroupParameter.Read(parameters, legacy))?.View ?? MarkerView.Plain;
    }

    private ReaperMarker? Resolve<T>(IReadOnlyDictionary<string, T> parameters, MarkerSnapshot snapshot) =>
        NumberOf(parameters) is { } n && n >= 1 ? snapshot.Resolve(n, ViewOf(parameters)) : null;

    private static int? NumberOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        Values.Get(parameters, NumberParameter) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value)
            : null;

    /// <summary>REAPER position string: "m3" = marker 3, "r3" = start of region 3.</summary>
    internal static string PositionOf(ReaperMarker marker) =>
        (marker.IsRegion ? "r" : "m") + marker.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// "Set": hands the job to ReaperControl_SetMarker.lua in one request (ExtState "MarkerSet", then the
    /// script): move this marker/region to the cursor, or create one - with the button's number as ID
    /// when the view uses IDs, named after the name filter if one is active. One undo step.
    /// </summary>
    private string SetJob<T>(ReaperMarker? marker, IReadOnlyDictionary<string, T> parameters)
    {
        var view = ViewOf(parameters);
        var region = marker?.IsRegion ?? view.Kind == MarkerKind.Regions;
        string[] fields =
        [
            region ? "R" : "M",
            marker?.Id.ToString(CultureInfo.InvariantCulture) ?? "",
            // New one: the button's number as ID where numbers are IDs, else REAPER picks one.
            marker is null && view.UsesIds && NumberOf(parameters) is { } n ? n.ToString(CultureInfo.InvariantCulture) : "",
            Values.Get(parameters, RegionModeParameter) ?? "move",
            Values.Get(parameters, SetModeParameter) == "edit" ? "1" : "0",
            marker is null ? view.Filter?.Text ?? "" : "" // name of a new one: the filter text
        ];
        return string.Join('\t', fields);
    }

    private sealed class Executor(
        MarkerButtonAction action,
        ReaperConnection connection,
        MarkerMonitor markers,
        ILogger logger) : IActionExecutor
    {
        public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var client = connection.Client;
            if (client is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotConfigured, "REAPER connection is not set up yet.");
            }

            // Resolve against the markers the button shows, so the press goes where the label says.
            var snapshot = markers.Get();
            var marker = snapshot is null ? null : action.Resolve(context.Parameters, snapshot);

            if (Values.Get(context.Parameters, OnPressParameter) == "set")
            {
                return snapshot is null
                    ? ActionResult.Failed(ActionErrorCodes.NotConnected, "The markers are not loaded yet.")
                    : await SetAsync(client, marker, context);
            }

            if (marker is null)
            {
                logger.LogInformation(
                    "Marker button: nothing for number '{Number}' ({Known} markers/regions known)",
                    Values.Get(context.Parameters, NumberParameter) ?? "none",
                    snapshot?.Items.Count.ToString() ?? "not loaded yet");
                return ActionResult.Success(); // empty slot: nothing to do
            }

            try
            {
                await client.SendAsync([$"SET/POS_STR/{PositionOf(marker!)}"], context.CancellationToken);
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

        private async Task<ActionResult> SetAsync(
            ReaperWebClient client, ReaperMarker? marker, ActionExecutionContext context)
        {
            var scriptId = connection.Catalog.FindScriptCommandId(SetScriptFileName, ReaperScriptFiles.InstallFolder);
            if (scriptId is null)
            {
                return ActionResult.Failed(ActionErrorCodes.NotFound,
                    $"{SetScriptFileName} is not in REAPER's action list: install the scripts and run " +
                    "ReaperControl_Setup.lua once.");
            }

            try
            {
                // One request, handled in order: hand over the job, then run the script.
                await client.SendEncodedAsync(
                    [
                        ReaperWebClient.EncodeSetExtState("ReaperControl", "MarkerSet",
                            action.SetJob(marker, context.Parameters)),
                        Uri.EscapeDataString(scriptId)
                    ],
                    context.CancellationToken);
                markers.RequestRefresh();
                return ActionResult.Success();
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Setting {Marker} failed", MarkerMonitor.Key(marker));
                return ActionResult.Failed(ActionErrorCodes.NotConnected,
                    "REAPER is not reachable. Is it running with the web interface enabled?");
            }
        }
    }
}

/// <summary>
/// "Marker filter": activates a name filter in a filter group (press again to clear it). Shows the
/// filter text as label, lit while the filter is active. One name filter per group at a time.
/// </summary>
public sealed class MarkerFilterAction(MarkerFilterState filters, MarkerMonitor markers)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "marker-filter";

    private const string TextParameter = "text";
    private const string MatchParameter = "match";

    public string Id => ActionId;

    public LocalizedText Name => "Marker filter";

    public LocalizedText Description =>
        "Shows only matching markers/regions on the buttons following this filter group. Press again to clear.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Text(TextParameter, "Filter text", "E.g. #S or Jingle. Not case-sensitive.",
            placeholder: "#S", required: true),
        ActionParameter.Choice(MatchParameter,
            [
                new ActionParameterOption { Value = "contains", Label = "Name contains the text" },
                new ActionParameterOption { Value = "starts-with", Label = "Name starts with the text" }
            ],
            "Match", defaultValue: "contains"),
        MarkerGroupParameter.ForFilters("The group this filter belongs to.")
    ];

    // The filter only changes through these buttons, which notify right away.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(5);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        // Always answer with states (see MarkerButtonAction): a placeholder until a text is entered.
        var own = FilterOf(parameters);
        var group = GroupOf(parameters);
        var label = (own?.Text ?? "Filter") + (group.Name == "A" ? "" : $"\nGroup {group.Name}");
        var baseId = BaseId(own, group);
        var onId = baseId + "-on";
        var offId = baseId + "-off";

        IReadOnlyList<ActionStateDefinition> states =
        [
            new(onId, $"{label} (active)")
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = label, BackgroundColor = "#2b6cb0", LabelColor = "#ffffff"
                }
            },
            new(offId, label)
            {
                DefaultAppearance = new ActionStateAppearance { Label = label }
            }
        ];

        var active = own is not null && own.Equals(group.Current);
        return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(states, active ? onId : offId));
    }

    public IActionExecutor CreateExecutor() => new Executor(this, markers);

    private MarkerFilterGroup GroupOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        filters.Group(MarkerGroupParameter.Read(parameters, "A")) ?? filters.A;

    // Group A keeps the ids from before filter groups, so existing buttons keep their look.
    private static string BaseId(MarkerFilter? own, MarkerFilterGroup group) =>
        TextStateIds.For("filter",
            (group.Name == "A" ? "" : group.Name + "\u0001") + (own is null ? "" : $"{own.Match}\u0001{own.Text}"));

    private static MarkerFilter? FilterOf<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var text = Values.Get(parameters, TextParameter);
        if (text is null)
        {
            return null;
        }

        var match = Values.Get(parameters, MatchParameter) == "starts-with"
            ? MarkerMatch.StartsWith
            : MarkerMatch.Contains;
        return new MarkerFilter(text, match);
    }

    private sealed class Executor(MarkerFilterAction action, MarkerMonitor markers) : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var own = FilterOf(context.Parameters);
            if (own is null)
            {
                return Task.FromResult(ActionResult.Failed(ActionErrorCodes.InvalidParameter, "No filter text set."));
            }

            var group = action.GroupOf(context.Parameters);
            var now = group.Toggle(own);
            // Marker buttons and the other filter buttons change with it.
            markers.NotifyButtonsChanged();

            return Task.FromResult(ActionResult.Success(BaseId(own, group) + (now is null ? "-off" : "-on")));
        }
    }
}

/// <summary>
/// "Marker type": what the buttons following a filter group show - markers, regions or both. Either
/// sets one kind (lit while it is active) or cycles Markers → Regions → Both with every press (the
/// label shows the current kind). After a start every group shows markers only.
/// </summary>
public sealed class MarkerTypeAction(MarkerFilterState filters, MarkerMonitor markers)
    : IActionDefinition, IStateProviderActionDefinition
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "marker-type";

    private const string ModeParameter = "mode";
    private const string Cycle = "cycle";

    // Mode values (persisted) and the kind they set.
    private static readonly IReadOnlyList<(string Value, MarkerKind Kind)> Kinds =
    [
        ("markers", MarkerKind.Markers),
        ("regions", MarkerKind.Regions),
        ("both", MarkerKind.Both)
    ];

    // Button color per kind on the cycling button, and for "active" on the fixed ones.
    private static string ColorOf(MarkerKind kind) => kind switch
    {
        MarkerKind.Regions => "#6b46c1",
        MarkerKind.Both => "#2f855a",
        _ => "#2b6cb0"
    };

    public string Id => ActionId;

    public LocalizedText Name => "Marker type";

    public LocalizedText Description =>
        "Markers, regions or both on the buttons following a filter group - set one, or switch with every press.";

    public IReadOnlyList<ActionParameter> Parameters { get; } =
    [
        ActionParameter.Choice(ModeParameter,
            [
                new ActionParameterOption { Value = Cycle, Label = "Switch: Markers → Regions → Both" },
                new ActionParameterOption { Value = "markers", Label = "Markers" },
                new ActionParameterOption { Value = "regions", Label = "Regions" },
                new ActionParameterOption { Value = "both", Label = "Markers + Regions" }
            ],
            "Mode", defaultValue: Cycle, required: true),
        MarkerGroupParameter.ForFilters("The group whose kind this button sets.")
    ];

    // The kind only changes through these buttons, which notify right away.
    public TimeSpan StatePollInterval => TimeSpan.FromSeconds(5);

    public Task<ActionStateSnapshot?> GetActionStateAsync(
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var group = GroupOf(parameters);
        var suffix = group.Name == "A" ? "" : $"\nGroup {group.Name}";

        if (FixedKind(parameters) is not { } kind)
        {
            // Cycling: one state per kind, the current one active.
            var states = Kinds.Select(k =>
            {
                var label = MarkerGroupParameter.KindName(k.Kind) + suffix;
                return new ActionStateDefinition(StateId(group, Cycle, k.Value), label)
                {
                    DefaultAppearance = new ActionStateAppearance
                    {
                        Label = label, BackgroundColor = ColorOf(k.Kind), LabelColor = "#ffffff"
                    }
                };
            }).ToList();
            var current = Kinds.First(k => k.Kind == group.Kind).Value;
            return Task.FromResult<ActionStateSnapshot?>(
                new ActionStateSnapshot(states, StateId(group, Cycle, current)));
        }

        var name = MarkerGroupParameter.KindName(kind.Kind) + suffix;
        var onId = StateId(group, kind.Value, "on");
        var offId = StateId(group, kind.Value, "off");
        IReadOnlyList<ActionStateDefinition> fixedStates =
        [
            new(onId, $"{name} (active)")
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = name, BackgroundColor = ColorOf(kind.Kind), LabelColor = "#ffffff"
                }
            },
            new(offId, name)
            {
                DefaultAppearance = new ActionStateAppearance
                {
                    Label = name, BackgroundColor = "#2d3748", LabelColor = "#e2e8f0"
                }
            }
        ];
        return Task.FromResult<ActionStateSnapshot?>(
            new ActionStateSnapshot(fixedStates, group.Kind == kind.Kind ? onId : offId));
    }

    public IActionExecutor CreateExecutor() => new Executor(this, markers);

    private MarkerFilterGroup GroupOf<T>(IReadOnlyDictionary<string, T> parameters) =>
        filters.Group(MarkerGroupParameter.Read(parameters, "A")) ?? filters.A;

    /// <summary>The kind a fixed button sets, or null for the cycling button.</summary>
    private static (string Value, MarkerKind Kind)? FixedKind<T>(IReadOnlyDictionary<string, T> parameters)
    {
        var mode = Values.Get(parameters, ModeParameter) ?? Cycle;
        return Kinds.Any(k => k.Value == mode) ? Kinds.First(k => k.Value == mode) : null;
    }

    private static string StateId(MarkerFilterGroup group, string mode, string part) =>
        $"type-{group.Name.ToLowerInvariant()}-{mode}-{part}";

    private sealed class Executor(MarkerTypeAction action, MarkerMonitor markers) : IActionExecutor
    {
        public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
        {
            var group = action.GroupOf(context.Parameters);
            string expected;
            if (FixedKind(context.Parameters) is { } kind)
            {
                group.SetKind(kind.Kind);
                expected = StateId(group, kind.Value, "on");
            }
            else
            {
                var now = group.CycleKind();
                expected = StateId(group, Cycle, Kinds.First(k => k.Kind == now).Value);
            }

            // Marker buttons and the other type buttons change with it.
            markers.NotifyButtonsChanged();
            return Task.FromResult(ActionResult.Success(expected));
        }
    }
}
