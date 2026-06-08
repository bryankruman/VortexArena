using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Screenshots tab — a faithful C# port of <c>XonoticScreenshotBrowserTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_media_screenshot.qc). The QC builds a filter box, an
/// <c>XonoticScreenshotList</c> (engine-enumerated screenshots), "Auto screenshot scoreboard"
/// (<c>cl_autoscreenshot</c> bit 1, sendCvars) + Refresh, and an "Open in the viewer" button that
/// pushes the screenshot viewer dialog. We also surface the viewer's slideshow/zoom transport
/// controls from <c>XonoticScreenshotViewerDialog_fill</c> (dialog_media_screenshot_viewer.qc):
/// zoom −/+/Reset, Previous/Next, Slide show.
///
/// FAITHFUL UI NOW: XonoticGodot has no screenshot enumeration / image viewer backend, so the list is an
/// empty <see cref="ItemList"/> with an honest note and the viewer transport buttons are inert. Only
/// the <c>cl_autoscreenshot</c> flag checkbox is live (binds the same cvar/bit the QC binds).
/// </summary>
public partial class DialogMediaScreenshot : Control
{
    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 16);
        AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        // Filter (QC makeXonoticInputBox + ScreenshotList_Filter_Would_Change — no cvar, inert).
        var filter = new LineEdit { PlaceholderText = "Filter screenshots…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(Ui.Row("Filter:", filter, 80f));

        var topButtons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topButtons.AddThemeConstantOverride("separation", 10);
        // QC: makeXonoticCheckBoxEx(2, 1, "cl_autoscreenshot", _("Auto screenshot scoreboard")) — bit 1, live.
        var autoshot = Widgets.FlagCheckBox("cl_autoscreenshot", 1, "Auto screenshot scoreboard");
        autoshot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topButtons.AddChild(autoshot);
        // QC: makeXonoticButton(_("Refresh"), …) onClick=ScreenshotList_Refresh_Click (inert here).
        topButtons.AddChild(Ui.Button("Refresh", () => MenuCommand.Run("menu_cmd sync")));
        box.AddChild(topButtons);

        // The screenshot list (QC: slist spans the middle). Backend absent → empty + note.
        var list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        box.AddChild(list);
        box.AddChild(Ui.Label("(screenshot list — screenshot enumeration / image viewer backend pending)"));

        // QC: makeXonoticButton(_("Open in the viewer"), …) onClick=StartScreenshot_Click (opens the viewer).
        box.AddChild(Ui.Button("Open in the viewer", () => MenuCommand.Run("menu_showscreenshotviewer")));

        // Viewer transport controls (from XonoticScreenshotViewerDialog_fill): zoom −/+/Reset, Previous/Next,
        // Slide show. The viewer's image pane + slideshow are backend-driven, so these are inert.
        box.AddChild(Ui.Spacer(6f));
        box.AddChild(Ui.Header("Viewer"));
        var viewer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        viewer.AddThemeConstantOverride("separation", 8);
        viewer.AddChild(Ui.Button("-", () => MenuCommand.Run("menu_screenshotviewer_zoomout")));   // decreaseZoom_Click
        viewer.AddChild(Ui.Button("+", () => MenuCommand.Run("menu_screenshotviewer_zoomin")));    // increaseZoom_Click
        viewer.AddChild(Ui.Button("Reset", () => MenuCommand.Run("menu_screenshotviewer_zoomreset"))); // resetZoom_Click
        viewer.AddChild(Ui.Button("Previous", () => MenuCommand.Run("menu_screenshotviewer_prev"))); // prevScreenshot_Click
        viewer.AddChild(Ui.Button("Next", () => MenuCommand.Run("menu_screenshotviewer_next")));     // nextScreenshot_Click
        viewer.AddChild(Ui.Button("Slide show", () => MenuCommand.Run("menu_screenshotviewer_slideshow"))); // toggleSlideShow_Click
        box.AddChild(viewer);
    }
}
