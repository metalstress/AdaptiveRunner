namespace SnowRunnerApex5;

internal sealed record FeedbackDecision(string LeftReason, string RightReason,
    long ShiftUntil = 0, long SlipCueUntil = 0, bool ShiftActive = false,
    bool SlipActive = false, string? ShiftCause = null);

internal sealed class Feedback
{
    private const int ShiftDurationMs = 160;
    // Flydigi's installed Forza preset: mode 2, [50, 1, 200, 1].
    // This is a bounded trigger pulse, not the grip-rumble command.
    private static readonly TriggerEffect ShiftPulse = new(2, 50, 1, 200, 1);
    private ulong previousVehicle;
    private int previousGear;
    private byte? previousGearMode;
    private long shiftUntil;
    private string? shiftCause;
    private long slipStarted;
    private long slipCueUntil;
    private long nextSlipCue;
    private bool slipping;
    private double smoothedGas;
    public FeedbackDecision Decision { get; private set; } = new("inactive", "inactive");

    public (TriggerEffect Left, TriggerEffect Right) Calculate(
        TruckState? truck, PhysicsState? physics, byte leftPedal, byte rightPedal, bool active,
        double intensity, long now)
    {
        if (!active || truck is null || !truck.Driving)
        {
            Reset();
            return (TriggerEffect.Off, TriggerEffect.Off);
        }

        bool sameTruck = previousVehicle == truck.Vehicle;
        if (!sameTruck)
            Reset();
        bool gearChanged = truck.Gear != previousGear;
        bool modeChanged = truck.GearMode != previousGearMode;
        bool knownModes = previousGearMode is >= 0 and <= 5 && truck.GearMode is >= 0 and <= 5;
        bool lowRangeChanged = modeChanged && knownModes &&
            (previousGearMode is >= 3 and <= 5 || truck.GearMode is >= 3 and <= 5);
        if (sameTruck && (gearChanged || modeChanged))
        {
            bool eligible = truck.Gear > 0 && previousGear > 0 && (gearChanged || lowRangeChanged);
            Program.Trace("gear_transition", new { From = previousGear, To = truck.Gear,
                FromGearMode = previousGearMode, ToGearMode = truck.GearMode, truck.GearModeName,
                NumberChanged = gearChanged, LowRangeChanged = lowRangeChanged,
                RawGearSelected = physics?.GearSelected, Source = "NextGen.Gear+GearMode",
                truck.Vehicle, truck.Tick, EligibleForShiftPulse = eligible, AtMonotonicMs = now });
            if (eligible)
            {
                shiftUntil = now + ShiftDurationMs;
                shiftCause = lowRangeChanged ? "low_range_change" : "gear_number_change";
                Program.Trace("shift_window_started", new { From = previousGear, To = truck.Gear,
                    FromGearMode = previousGearMode, ToGearMode = truck.GearMode,
                    Cause = shiftCause,
                    DurationMs = ShiftDurationMs, EndsAtMonotonicMs = shiftUntil,
                    Preset = ShiftPulse, PresetSource = "Flydigi Forza config.default.json" });
            }
        }
        previousVehicle = truck.Vehicle;
        previousGear = truck.Gear;
        previousGearMode = truck.GearMode;

        double rpm = Math.Clamp(truck.Rpm / Math.Max(1200, truck.Redline), 0, 1.25);
        double boost = Math.Clamp(truck.Boost / Math.Max(0.5, truck.BoostTop), 0, 1);
        double speed = Math.Abs(truck.Speed) / 3.6;
        double excess = physics is null ? 0 : physics.WheelSpeed - speed;
        double slipRatio = excess / Math.Max(speed, 1.0);
        bool spinCandidate = physics?.GroundedDrivenWheels >= 2 &&
            excess > (slipping ? 0.5 : 0.9) && slipRatio > (slipping ? 0.2 : 0.45);
        if (!spinCandidate || Math.Max(leftPedal, rightPedal) < 32)
        {
            slipStarted = 0;
            slipping = false;
            slipCueUntil = 0;
            nextSlipCue = 0;
        }
        else
        {
            if (slipStarted == 0)
                slipStarted = now;
            slipping = now - slipStarted >= 400;
        }
        if (slipping && now >= nextSlipCue)
        {
            slipCueUntil = now + 180;
            nextSlipCue = now + 1400;
        }
        double load = Math.Clamp(physics?.Pull ?? boost, 0, 1);
        double targetGas = 8 + rpm * 5 + boost * 20 + load * 15;
        smoothedGas += (targetGas - smoothedGas) * 0.15;

        TriggerEffect Gas(byte pedal)
        {
            if (pedal < 10 || !truck.EngineOn)
                return TriggerEffect.Off;
            // A fixed pulse is sent once per effect transition. Changing its
            // parameters every frame would restart the controller's effect.
            if (now < shiftUntil)
                return ShiftPulse;
            if (now < slipCueUntil)
                return Resistance(smoothedGas + 90, intensity);
            double strength = smoothedGas * (0.7 + pedal / 255.0 * 0.3);
            return Resistance(strength, intensity);
        }

        TriggerEffect Brake(byte pedal)
        {
            if (pedal < 10)
                return TriggerEffect.Off;
            byte resistance = (byte)Math.Clamp(Math.Round(
                (10 + 40 * Math.Pow(pedal / 255.0, 1.7)) * intensity), 1, 65);
            return new TriggerEffect(1, 28, resistance);
        }

        // The signed drivetrain command is also positive during forward
        // braking and negative while braking in reverse. It cannot identify
        // a pedal's function. Read the actual gearbox and brake-light state.
        bool autoReverse = physics is not null
            ? physics.Automatic && physics.GearSelected < 0
            : truck.Gear < 0;
        bool leftIsGas = autoReverse;
        bool rightIsBrake = autoReverse;
        if (physics?.BrakeLights == true)
        {
            if (leftPedal >= 10 && rightPedal < 10)
                leftIsGas = false;
            if (rightPedal >= 10 && leftPedal < 10)
                rightIsBrake = true;
        }
        string Reason(byte pedal, bool isGas) => pedal < 10 ? "released" :
            !isGas ? "brake_resistance" : !truck.EngineOn ? "engine_off" :
            now < shiftUntil ? "gear_shift_pulse" :
            now < slipCueUntil ? "wheelspin_pressure" : "throttle_resistance";
        Decision = new(Reason(leftPedal, leftIsGas), Reason(rightPedal, !rightIsBrake),
            shiftUntil, slipCueUntil, now < shiftUntil, now < slipCueUntil,
            now < shiftUntil ? shiftCause : null);
        return (leftIsGas ? Gas(leftPedal) : Brake(leftPedal),
            rightIsBrake ? Brake(rightPedal) : Gas(rightPedal));
    }

    private static TriggerEffect Resistance(double strength, double intensity) =>
        new(1, 20, (byte)Math.Clamp(Math.Round(strength * intensity), 1, 190));

    public void Reset()
    {
        previousVehicle = 0;
        previousGear = 0;
        previousGearMode = null;
        shiftUntil = 0;
        shiftCause = null;
        slipStarted = 0;
        slipCueUntil = 0;
        nextSlipCue = 0;
        slipping = false;
        smoothedGas = 0;
        Decision = new("inactive", "inactive");
    }
}
