using MacroDeck.Localization;
using Microsoft.Extensions.Logging;
using ReaperControl.Reaper;

namespace ReaperControl.Actions;

/// <summary>
/// "View": shows/hides one of REAPER's windows from the View menu (Routing Matrix, Track Manager,
/// ...), chosen in the button's settings. Plain toggle without on/off feedback; the button shows the
/// window's name.
/// </summary>
public sealed class InsertAction(
    ReaperConnection connection,
    CommandStateMonitor stateMonitor,
    ILogger<InsertAction> logger)
    : CommandListAction(connection, stateMonitor, logger)
{
    // Persisted in users' profiles - never rename after release.
    public const string ActionId = "insert";

    public override string Id => ActionId;

    public override LocalizedText Name => "Insert";

    public override LocalizedText Description => "Inserts.";

    protected override bool ShowsState => false;



 // The one field of every marker position: plain insert or insert and/or edit.
    private static readonly IReadOnlyList<ListField> MarkerModeField =
    [
        new("mode", "Mode", [("insert", "Insert"), ("edit", "Insert or edit")]),
    ];

    protected override IReadOnlyList<ListCommand> Commands { get; } =
    [
        new("insert-subproject", "Insert Subproject", "41049"),
        new("region-timeselction", "Insert Region from Timeselection", "40174"),
        new("region-item", "Insert Region from Item", "40348"),
        new("region-timeselction-edit", "Insert Region from Timeselection and edit", "40306"),
        new("region-item-edit", "Insert Region from Item and edit", "40393"),
        new("insert-marker", "Insert Marker", "40157",
            OptionsLabel: "Position",
            Options:
            [
                new("marker-pos-current", "Current position", Fields: MarkerModeField, Variants:
                [
                    //  key                   button label                                  id       Mode
                    new("marker",             "Insert Marker",                              "40157", ["insert"]),
                    new("marker-prompt-name", "Insert/Edit Marker",                         "40171", ["edit"]),
                ]),
                new("marker-pos-edit", "Edit cursor", Fields: MarkerModeField, Variants:
                [
                    new("marker-edit",        "Insert Marker at Edit Cursor",               "47306", ["insert"]),
                    new("marker-edit-e",      "Insert/Edit Marker at Edit Cursor",          "47307",  ["edit"]),
                ]),
                new("marker-pos-mouse", "Mouse position", Fields: MarkerModeField, Variants:
                [
                    new("marker-mouse",       "Insert Marker at Mouse",                     "43582", ["insert"]),
                    new("marker-mouse-e",     "Insert/Edit Marker at Mouse",                "43583",  ["edit"]),
                ]),
                new("marker-pos-mouse-ns", "Mouse position (ignore snap)", Fields: MarkerModeField, Variants:
                [
                    new("marker-mouse-ns",    "Insert Marker at Mouse (no snap)",           "43580", ["insert"]),
                    new("marker-mouse-ns-e",  "Insert/Edit Marker at Mouse (no snap)",      "43581",  ["edit"]),
                ]),
            ]),
    ];
}
