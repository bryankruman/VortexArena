using System;
using Godot;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Game.Loaders;
using XonoticGodot.Game.Menu;

namespace XonoticGodot.Game;

/// <summary>
/// The Darkplaces-style loading screen: a full-screen overlay showing the <c>gfx/loading.tga</c>
/// background image (centered and aspect-fit scaled), a blue progress bar at the bottom, and a
/// status text line above it. Replaces the plain black connect overlay with the authentic Xonotic
/// loading experience.
///
/// <para>Created by <see cref="Shell"/> before a match enters the tree, and passed to
/// <see cref="Net.NetGame"/> (or <see cref="GameDemo"/>) which updates progress during its load
/// sequence and dismisses it when the player spawns.</para>
///
/// <para>Layout mirrors DP <c>SCR_DrawLoadingScreen</c> + <c>SCR_DrawLoadingStack</c>: the image
/// is scaled to fit the viewport (DP <c>scr_loadingscreen_scale_limit 2</c> — scale until the
/// last edge hits the screen edge, i.e. contain/aspect-fit), the progress bar is a solid blue
/// rect at the very bottom (<c>scr_loadingscreen_barcolor "0 0 1"</c>, height 8 console-pixels),
/// and the status text sits centered above it.</para>
/// </summary>
public sealed partial class LoadingScreen : Control
{
    // DP defaults
    private static readonly Color BarColor = new(0f, 0f, 1f); // scr_loadingscreen_barcolor "0 0 1"
    private const float BarHeight = 10f; // slightly larger than DP's 8 for Godot's higher-res viewports

    private TextureRect _background = null!;
    private ColorRect _barBg = null!;
    private ColorRect _bar = null!;
    private Label _statusLabel = null!;
    private Label _mapLabel = null!;

    private float _progress;
    private string _status = "Loading...";

    public override void _Ready()
    {
        // Full-rect black background (the image may not cover the whole screen at all aspect ratios)
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var blackBg = new ColorRect { Name = "BlackBg", Color = Colors.Black };
        blackBg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(blackBg);

        // The loading image — centered, aspect-fit (DP scr_loadingscreen_scale_limit 2: scale until
        // the image fills the viewport in one dimension, letterboxed in the other).
        _background = new TextureRect
        {
            Name = "Background",
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_background);

        // Try to load gfx/loading.tga from the mounted VFS
        LoadBackgroundImage();

        // Progress bar background (dark, full width at bottom)
        _barBg = new ColorRect
        {
            Name = "BarBg",
            Color = new Color(0f, 0f, 0f, 0.7f),
            AnchorLeft = 0f, AnchorRight = 1f,
            AnchorTop = 1f, AnchorBottom = 1f,
            OffsetTop = -BarHeight, OffsetBottom = 0f,
            OffsetLeft = 0f, OffsetRight = 0f,
        };
        AddChild(_barBg);

        // Progress bar fill (blue, grows from left)
        _bar = new ColorRect
        {
            Name = "Bar",
            Color = BarColor,
            AnchorLeft = 0f, AnchorRight = 0f,
            AnchorTop = 1f, AnchorBottom = 1f,
            OffsetTop = -BarHeight, OffsetBottom = 0f,
            OffsetLeft = 0f, OffsetRight = 0f,
        };
        AddChild(_bar);

        // Status text (centered, just above the progress bar)
        _statusLabel = new Label
        {
            Name = "Status",
            Text = _status,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            AnchorLeft = 0f, AnchorRight = 1f,
            AnchorTop = 1f, AnchorBottom = 1f,
            OffsetTop = -BarHeight - 28f, OffsetBottom = -BarHeight - 4f,
            OffsetLeft = 0f, OffsetRight = 0f,
        };
        AddChild(_statusLabel);

        // Map name (centered, above the status text)
        _mapLabel = new Label
        {
            Name = "MapName",
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            AnchorLeft = 0f, AnchorRight = 1f,
            AnchorTop = 1f, AnchorBottom = 1f,
            OffsetTop = -BarHeight - 52f, OffsetBottom = -BarHeight - 28f,
            OffsetLeft = 0f, OffsetRight = 0f,
        };
        AddThemeColorOverride(_mapLabel, "font_color", new Color(0.7f, 0.7f, 0.7f));
        AddChild(_mapLabel);

        ApplyProgress();
    }

    /// <summary>Update the progress bar and status text. <paramref name="fraction"/> is 0..1.</summary>
    public void UpdateProgress(float fraction, string status)
    {
        _progress = Mathf.Clamp(fraction, 0f, 1f);
        _status = status ?? "";
        if (IsInsideTree())
            ApplyProgress();
    }

    /// <summary>Set the map name displayed above the status text.</summary>
    public void SetMapName(string mapName)
    {
        if (_mapLabel is not null && IsInsideTree())
            _mapLabel.Text = string.IsNullOrWhiteSpace(mapName) ? "" : mapName;
    }

    private void ApplyProgress()
    {
        if (_bar is null || _statusLabel is null)
            return;

        // The bar fill width is a fraction of the viewport width
        float viewWidth = GetViewportRect().Size.X;
        _bar.OffsetRight = viewWidth * _progress;

        _statusLabel.Text = _status;
    }

    private void LoadBackgroundImage()
    {
        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs is null)
            return;

        try
        {
            // DP scr_loadingscreen_picture "gfx/loading" + random variant selection
            // We load the base image; multiple variants could be supported later via scr_loadingscreen_count
            string baseName = "gfx/loading";
            string? vpath = vfs.ResolveImage(baseName);
            if (vpath is null)
                return;

            byte[] bytes = vfs.ReadBytes(vpath);
            string ext = System.IO.Path.GetExtension(vpath).TrimStart('.').ToLowerInvariant();
            Image? img = ext == "tga" ? TgaDecoder.Decode(bytes) : null;
            if (img is null)
            {
                img = new Image();
                Error err = ext switch
                {
                    "png" => img.LoadPngFromBuffer(bytes),
                    "jpg" or "jpeg" => img.LoadJpgFromBuffer(bytes),
                    _ => img.LoadTgaFromBuffer(bytes),
                };
                if (err != Error.Ok)
                    return;
            }
            _background.Texture = ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LoadingScreen] failed to load background: {ex.Message}");
        }
    }

    private static void AddThemeColorOverride(Label label, string name, Color color)
    {
        label.AddThemeColorOverride(name, color);
    }
}
