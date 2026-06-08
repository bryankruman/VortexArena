using Godot;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Speedometer / physics panel — port of Base/.../qcsrc/client/hud/panel/physics.qc (HUD panel #15). The QC
/// version drew the player's current speed (2D or 3D), a speed progress bar, an acceleration bar (as a
/// fraction of gravity via <c>ACCEL2GRAV</c>), and the "top speed" / "jump speed" peak text — all read off
/// <c>csqcplayer.velocity</c> each frame, converted to the unit selected by <c>hud_speed_unit</c>.
///
/// This port keeps the faithful core: speed is <c>vlen(velocity)</c> of the local <see cref="Player"/> (2D
/// unless <see cref="SpeedVertical"/>), the acceleration is the per-frame change in |velocity| scaled by
/// <see cref="Accel2Grav"/> into a fraction of gravity (with the same moving-average smoothing), and the
/// top-speed / jump-speed peaks fade out over their hold time (QC <c>cos(time_frac * M_PI_2)</c>). We draw a
/// speed bar (filled to <c>speed / speed_max</c>) and an acceleration bar centered on zero, with the speed
/// number and unit, mirroring the default vertical layout.
///
/// Dropped from the 1:1 QC: the configure-mode demo values, the many bar-align / flip / progressbar-mode
/// cvars, the strafe-HUD-derived slick/on-ground accel-max selection, and the skinned progressbar images —
/// none change the readout the player needs.
/// </summary>
public partial class PhysicsPanel : HudPanel
{
    /// <summary>The local player; speed/accel are read from <see cref="Common.Framework.Entity.Velocity"/> each frame.</summary>
    public Player? Player { get; set; }

    /// <summary>QC <c>hud_panel_physics_speed_vertical</c>: include vertical velocity in the speed readout.</summary>
    public bool SpeedVertical { get; set; }

    /// <summary>QC <c>hud_panel_physics_speed_max</c>: speed (in qu/s) the speed bar treats as full.</summary>
    public float SpeedMax { get; set; } = 1800f;

    /// <summary>QC <c>hud_panel_physics_acceleration_max</c>: |accel| (fraction of gravity) the accel bar treats as full.</summary>
    public float AccelMax { get; set; } = 1.5f;

    /// <summary>QC <c>hud_panel_physics_topspeed</c>: show the fading top-speed peak text.</summary>
    public bool ShowTopSpeed { get; set; } = true;

    /// <summary>QC <c>hud_panel_physics_topspeed_time</c>: seconds the top-speed peak holds before fading.</summary>
    public float TopSpeedHold { get; set; } = 4f;

    /// <summary>
    /// QC <c>hud_speed_unit</c>: 1=qu/s, 2=m/s, 3=km/h, 4=mph, 5=knots. Selects the conversion factor + label
    /// (QC <c>GetSpeedUnitFactor</c> / <c>GetSpeedUnit</c>, client/main.qc).
    /// </summary>
    public int SpeedUnit { get; set; } = 1;

    /// <summary>QC <c>hud_panel_physics_speed_unit_show</c>: draw the unit label under the speed number.</summary>
    public bool ShowUnit { get; set; } = true;

    // QC ACCEL2GRAV (physics.qh): converts qu/s^2 to a fraction of real-world gravity.
    private const double Accel2Grav = 0.00259007918096393775;

    // QC acc_prev_vel / acc_prev_time / acc_avg: state for the per-frame acceleration computation.
    private NVec3 _accPrevVel;
    private double _accPrevTime;
    private float _accAvg;
    private bool _haveAccPrev;

    // QC top_speed / top_speed_time: peak speed and when it was set.
    private float _topSpeed;
    private double _topSpeedTime;

    // QC hud_panel_physics_acceleration_movingaverage: smoothing window (tenths of a second).
    private const float AccelMovingAverage = 0f; // 0 = off, matching the Xonotic default

    private double _localClock;

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    /// <summary>QC <c>GetSpeedUnitFactor</c> (client/main.qc): qu/s -> selected unit.</summary>
    private static float UnitFactor(int unit) => unit switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 3.6f,
        4 => 0.0254f * 3.6f * 0.6213711922f,
        5 => 0.0254f * 1.943844492f,
        _ => 1.0f, // 1 / default: qu/s
    };

    /// <summary>QC <c>GetSpeedUnit</c> (client/main.qc): unit label.</summary>
    private static string UnitLabel(int unit) => unit switch
    {
        2 => "m/s",
        3 => "km/h",
        4 => "mph",
        5 => "knots",
        _ => "qu/s",
    };

    protected override void DrawPanel()
    {
        if (Player is null) return;
        if (Player.GetResource(ResourceType.Health) <= 0f) return; // QC: nothing to show while dead

        NVec3 vel = Player.Velocity;
        float conv = UnitFactor(SpeedUnit);
        double now = CurrentTime();

        // QC: speed2d uses the XY plane; speed adds Z if hud_panel_physics_speed_vertical.
        float speed2d = Mathf.Floor(Length2D(vel) * conv + 0.5f);
        float speed = SpeedVertical
            ? Mathf.Floor(vel.Length() * conv + 0.5f)
            : speed2d;

        // QC acceleration: change in |velocity| over the frame, scaled into a fraction of gravity, optionally
        // smoothed by a moving average. Port of physics.qc lines ~108-126.
        float accel = 0f;
        if (_haveAccPrev)
        {
            float f = (float)(now - _accPrevTime);
            float dv = SpeedVertical
                ? vel.Length() - _accPrevVel.Length()
                : Length2D(vel) - Length2D(_accPrevVel);
            accel = dv * (1f / Mathf.Max(0.0001f, f)) * (float)Accel2Grav;

            if (AccelMovingAverage > 0f)
            {
                f = Mathf.Clamp(f * 10f / AccelMovingAverage, 0f, 1f);
                _accAvg = _accAvg * (1f - f) + accel * f;
                accel = _accAvg;
            }
        }
        _accPrevVel = vel;
        _accPrevTime = now;
        _haveAccPrev = true;

        // QC top speed peak: track the max and fade it over topspeed_time (cos easing).
        float topSpeedFade = 0f;
        if (ShowTopSpeed)
        {
            if (speed >= _topSpeed)
            {
                _topSpeed = speed;
                _topSpeedTime = now;
            }
            if (_topSpeed > 0f)
            {
                float tf = (float)((now - _topSpeedTime) / Mathf.Max(1f, TopSpeedHold));
                topSpeedFade = tf > 1f ? 0f : Mathf.Cos(tf * Mathf.Pi * 0.5f);
                if (topSpeedFade <= 0f) _topSpeed = 0f;
            }
        }

        DrawBackground();

        float pad = Padding;
        float x = pad;
        float w = Size2.X - pad * 2f;
        // Two stacked halves: speed (top), acceleration (bottom) — the default vertical layout.
        float halfH = (Size2.Y - pad * 2f) * 0.5f;
        float speedTop = pad;
        float accelTop = pad + halfH;

        // --- speed bar + number ---
        float speedMaxConv = Mathf.Max(1f, Mathf.Floor(SpeedMax * conv + 0.5f));
        var speedBar = new Rect2(x, speedTop + halfH * 0.55f, w, halfH * 0.35f);
        DrawBar(speedBar, speed / speedMaxConv, new Color(0.3f, 0.7f, 1f, 0.85f));

        int numSize = (int)Mathf.Clamp(halfH * 0.5f, 14f, 40f);
        DrawTextCentered(new Vector2(0f, speedTop), Size2.X, Mathf.RoundToInt(speed).ToString(),
            FgColor, numSize);

        if (ShowUnit)
        {
            int unitSize = (int)Mathf.Clamp(halfH * 0.22f, 9f, 16f);
            DrawTextCentered(new Vector2(0f, speedTop + halfH * 0.36f), Size2.X, UnitLabel(SpeedUnit),
                new Color(1f, 1f, 1f, 0.6f), unitSize);
        }

        // Top-speed peak marker on the speed bar (faded), plus its number.
        if (topSpeedFade > 0f && speed < _topSpeed)
        {
            float peakFrac = Mathf.Clamp(_topSpeed / speedMaxConv, 0f, 1f);
            float peakX = speedBar.Position.X + peakFrac * speedBar.Size.X;
            DrawRect(new Rect2(peakX - 1f, speedBar.Position.Y, 2f, speedBar.Size.Y),
                new Color(1f, 0.3f, 0.3f, topSpeedFade * 0.9f));
            int ts = (int)Mathf.Clamp(halfH * 0.28f, 10f, 18f);
            DrawTextRight(x + w, speedTop, w, Mathf.RoundToInt(_topSpeed).ToString(),
                new Color(1f, 0.4f, 0.4f, topSpeedFade), ts);
        }

        // --- acceleration bar (centered on zero) + number ---
        var accelArea = new Rect2(x, accelTop + halfH * 0.3f, w, halfH * 0.4f);
        DrawCenteredBar(accelArea, Mathf.Clamp(accel / Mathf.Max(0.0001f, AccelMax), -1f, 1f),
            accel >= 0f ? new Color(0.3f, 1f, 0.3f, 0.85f) : new Color(1f, 0.3f, 0.2f, 0.85f));

        int accelSize = (int)Mathf.Clamp(halfH * 0.32f, 11f, 22f);
        // QC physics.qc: avoid ftos_decimals printing a negative zero ("-0.00").
        if (accel > -0.005f && accel < 0f) accel = 0f;
        DrawTextCentered(new Vector2(0f, accelTop), Size2.X,
            accel.ToString("0.00") + "g", new Color(1f, 1f, 1f, 0.8f), accelSize);
    }

    /// <summary>QC <c>vlen(vec2(v))</c>: planar (XY) speed.</summary>
    private static float Length2D(NVec3 v) => Mathf.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>
    /// Draw a bar whose fill grows from the center to the left (negative) or right (positive), clamped to
    /// [-1,1] — the analogue of QC's centered acceleration progressbar (baralign == 2/3).
    /// </summary>
    private void DrawCenteredBar(Rect2 area, float signedFraction, Color fill)
    {
        DrawRect(area, new Color(0f, 0f, 0f, 0.35f));
        float cx = area.Position.X + area.Size.X * 0.5f;
        float half = area.Size.X * 0.5f;
        float fillW = Mathf.Abs(signedFraction) * half;
        if (fillW > 0f)
        {
            var r = signedFraction >= 0f
                ? new Rect2(cx, area.Position.Y, fillW, area.Size.Y)
                : new Rect2(cx - fillW, area.Position.Y, fillW, area.Size.Y);
            DrawRect(r, fill);
        }
        DrawRect(new Rect2(cx - 0.5f, area.Position.Y, 1f, area.Size.Y), new Color(1f, 1f, 1f, 0.3f));
        DrawRect(area, new Color(1f, 1f, 1f, 0.15f), filled: false, width: 1f);
    }
}
