namespace ReaperControl.Reaper;

/// <summary>Which of REAPER's markers and regions a view shows.</summary>
public enum MarkerKind
{
    Markers,
    Regions,
    Both
}

/// <summary>How a filter text is compared with marker/region names (case-insensitive).</summary>
public enum MarkerMatch
{
    Contains,
    StartsWith
}

public sealed record MarkerFilter(string Text, MarkerMatch Match)
{
    public bool Matches(ReaperMarker marker) => Match == MarkerMatch.StartsWith
        ? marker.Name.StartsWith(Text, StringComparison.OrdinalIgnoreCase)
        : marker.Name.Contains(Text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a marker button shows: a kind (markers / regions / both) and optionally a name filter.
/// <see cref="Plain" /> (markers, no name filter) is the view of buttons that follow no filter group.
/// </summary>
public sealed record MarkerView(MarkerKind Kind, MarkerFilter? Filter)
{
    public static MarkerView Plain { get; } = new(MarkerKind.Markers, null);

    public bool Matches(ReaperMarker m) =>
        (Kind == MarkerKind.Both || m.IsRegion == (Kind == MarkerKind.Regions))
        && (Filter is null || Filter.Matches(m));

    /// <summary>
    /// Whether a numbered button means a real ID: one kind and no name filter. Otherwise ("Both" or a
    /// name filter) number n is the n-th entry of the view in time order.
    /// </summary>
    public bool UsesIds => Filter is null && Kind != MarkerKind.Both;
}

/// <summary>Markers and regions of the current project and the play/edit cursor position.</summary>
/// <param name="Items">Markers and regions, sorted by position.</param>
/// <param name="Position">Play position while playing, edit cursor otherwise (seconds).</param>
public sealed record MarkerSnapshot(IReadOnlyList<ReaperMarker> Items, double Position)
{
    /// <summary>Tolerance for "the cursor stands exactly on a marker" (float positions).</summary>
    public const double Tolerance = 0.0005;

    /// <summary>The last marker at or before the cursor (regions do not count), if any.</summary>
    public int? CurrentMarkerId { get; } =
        Items.LastOrDefault(m => !m.IsRegion && m.Start <= Position + Tolerance)?.Id;

    /// <summary>
    /// Current: a marker while it is the last one at or before the cursor, a region while the cursor is
    /// inside it. With "both", a marker and a region can be current at the same time.
    /// </summary>
    public bool IsCurrent(ReaperMarker m) => m.IsRegion
        ? m.Start <= Position + Tolerance && Position < (m.End ?? m.Start) - Tolerance
        : m.Id == CurrentMarkerId;

    /// <summary>First entry of <paramref name="view" /> right of the cursor.</summary>
    public ReaperMarker? Next(MarkerView view) =>
        Items.FirstOrDefault(m => m.Start > Position + Tolerance && view.Matches(m));

    /// <summary>Last entry of <paramref name="view" /> left of the cursor; one exactly at the cursor does not count.</summary>
    public ReaperMarker? Previous(MarkerView view) =>
        Items.LastOrDefault(m => m.Start < Position - Tolerance && view.Matches(m));

    /// <summary>
    /// The entry a numbered button means: with <see cref="MarkerView.UsesIds" /> the marker/region with
    /// this ID, otherwise the n-th entry of the view (1 = first).
    /// </summary>
    public ReaperMarker? Resolve(int n, MarkerView view) => view.UsesIds
        ? Items.FirstOrDefault(m => m.Id == n && view.Matches(m))
        : Items.Where(view.Matches).ElementAtOrDefault(n - 1);
}

/// <summary>One filter group (A-D): name filter, kind and the next/previous "follow filter" switch.</summary>
public sealed class MarkerFilterGroup(string name)
{
    private MarkerFilter? _filter;
    private int _kind = (int)MarkerKind.Markers; // default after start: markers only
    private volatile bool _navigationFollowsFilter;

    /// <summary>"A" … "D".</summary>
    public string Name { get; } = name;

    /// <summary>The active name filter, or null.</summary>
    public MarkerFilter? Current => Volatile.Read(ref _filter);

    public MarkerKind Kind => (MarkerKind)Volatile.Read(ref _kind);

    public MarkerView View => new(Kind, Current);

    /// <summary>
    /// Switch for "Go to next / previous marker" buttons set to "Follow the switch button" in this group.
    /// </summary>
    public bool NavigationFollowsFilter => _navigationFollowsFilter;

    public bool ToggleNavigationFollowsFilter() => _navigationFollowsFilter = !_navigationFollowsFilter;

    /// <summary>Activates <paramref name="filter" />, or clears it if it is already active. Returns the new filter.</summary>
    public MarkerFilter? Toggle(MarkerFilter filter)
    {
        while (true)
        {
            var current = Current;
            var next = filter.Equals(current) ? null : filter;
            if (Interlocked.CompareExchange(ref _filter, next, current) == current)
            {
                return next;
            }
        }
    }

    public void SetKind(MarkerKind kind) => Volatile.Write(ref _kind, (int)kind);

    /// <summary>Markers → Regions → Both → Markers …; returns the new kind.</summary>
    public MarkerKind CycleKind()
    {
        var next = (MarkerKind)(((int)Kind + 1) % 3);
        SetKind(next);
        return next;
    }
}

/// <summary>
/// The four filter groups A-D that filter buttons set and marker buttons follow. Lost when the plugin
/// restarts (all groups: markers only, no name filter).
/// </summary>
public sealed class MarkerFilterState
{
    public static readonly IReadOnlyList<string> GroupNames = ["A", "B", "C", "D"];

    private readonly Dictionary<string, MarkerFilterGroup> _groups =
        GroupNames.ToDictionary(n => n, n => new MarkerFilterGroup(n), StringComparer.OrdinalIgnoreCase);

    public IEnumerable<MarkerFilterGroup> Groups => _groups.Values;

    /// <summary>The group with this name ("A" … "D"), or null for none / unknown.</summary>
    public MarkerFilterGroup? Group(string? name) =>
        name is not null && _groups.TryGetValue(name, out var group) ? group : null;

    public MarkerFilterGroup A => _groups["A"];

    /// <summary>Group A's switch (variable reaper_marker_nav_filtered, from before groups existed).</summary>
    public bool NavigationFollowsFilter => A.NavigationFollowsFilter;
}
