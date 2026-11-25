using MacroDeck.Localization;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "FX": switches FX on and off - bypass and online/offline for monitoring FX, the master, tracks and
/// items, chosen in the button's settings. Lit (orange) while REAPER reports the switch as on, as far as
/// the action has a toggle state ("State" column of the action list).
/// </summary>
/// <remarks>
/// Showing FX windows stays in "View" (Monitoring FX → Show Chain); "FX" is for what turns FX on or off.
/// To add a switch: one line (key, label, command id), or an option under an existing entry.
/// </remarks>
public sealed class FxAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<FxAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "fx";

    // Orange while bypassed / offline, dark otherwise.
    private const string On = "#dd6b20";
    private const string Off = "#4a3a2a";

    public override string Id => ActionId;

    public override LocalizedText Name => "FX";

    public override LocalizedText Description =>
        "Bypasses FX or takes them offline: monitoring FX, master, tracks or items.";

    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        new("monitoring-fx-bypass", "Monitoring FX Bypass", "41884", OnColor: On, OffColor: Off),
        new("master-fx-bypass", "Master FX Bypass", "16", OnColor: On, OffColor: Off),
        new("track-fx", "Selected Tracks FX", "8", OnColor: On, OffColor: Off,
            OptionsLabel: "Switch",
            Options:
            [
                new("track-fx-bypass", "Track FX Bypass", "8"),
                new("track-fx-offline", "Track FX Online/Offline", "40535"),
            ]),
        new("all-tracks-fx-bypass", "All Tracks FX Bypass", "40344", OnColor: On, OffColor: Off),
        //new("item-fx-bypass", "Item FX Bypass", "_S&M_TGL_TAKEFX_BYP", OnColor: On, OffColor: Off),
    ];
}
