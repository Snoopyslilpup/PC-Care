using System;
using WhateverITPCCare.Helpers;

namespace WhateverITPCCare.Services;

/// <summary>
/// The result of a cooling request - deliberately honest about WHAT actually
/// happened. The companion never claims a fan was spun up when it was not.
/// </summary>
public sealed class CoolingResult
{
    /// <summary>True only when we actually changed a Windows setting that lets
    /// the hardware cool harder (the power plan). Never true just because we
    /// \"asked\".</summary>
    public bool ActionTaken { get; set; }
    /// <summary>True when direct fan-speed control is genuinely available on
    /// this PC. On the vast majority of machines this is false and we say so.</summary>
    public bool DirectFanControl { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Cooling helper. IMPORTANT: reading temperatures works on many PCs, but
/// directly driving the fans does NOT - it depends on the motherboard, the
/// laptop maker, the GPU, the firmware and whether an EC/vendor interface is
/// exposed. Windows has no universal fan API. So this class:
///
///  * reports fan-control compatibility truthfully (direct control is treated
///    as unavailable unless a vendor interface is detected - which, on a plain
///    Windows box, it is not);
///  * does the one cooling action that IS universally safe and real - raising
///    the Windows power plan so the firmware is allowed to ramp the fans and
///    the chips run cooler - and only reports success when powercfg really
///    switched the plan;
///  * otherwise tells the user to use their PC maker's own fan utility, rather
///    than pretending it moved a fan it cannot reach.
/// </summary>
public sealed class FanControl
{
    // Windows built-in power scheme GUIDs (same ones The Rig uses).
    private const string PlanBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PlanHigh     = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <summary>
    /// Whether this PC exposes a direct fan-speed interface we can safely drive.
    /// On a standard Windows install there is none, so we return false and the
    /// UI points the user at their motherboard / laptop vendor tool. (Kept as a
    /// method so a future vendor-specific backend can light it up without the
    /// rest of the app pretending in the meantime.)
    /// </summary>
    public bool DirectFanControlAvailable() => false;

    /// <summary>A plain-language, honest summary of what cooling control this
    /// PC supports, for display in the companion control panel.</summary>
    public readonly record struct FanCaps(bool CanControl, string Detail);

    /// <summary>
    /// Describe cooling capability truthfully. Direct fan-speed control is not
    /// available on a standard Windows box, so we report what we really can do
    /// (raise the power plan) rather than imply we can spin fans ourselves.
    /// </summary>
    public static FanCaps Describe()
    {
        bool direct = new FanControl().DirectFanControlAvailable();
        return direct
            ? new FanCaps(true, "a direct fan interface was detected on this PC.")
            : new FanCaps(false, "no direct fan interface on a standard Windows PC");
    }

    /// <summary>
    /// Ask for more cooling. Where we can, we raise the Windows power plan to
    /// High performance so the firmware fan curve is allowed to ramp up. We
    /// report exactly what we did - and if direct fan control is not available
    /// (the normal case) we say that too, instead of faking a fan change.
    /// </summary>
    public CoolingResult RequestMoreCooling(bool maximum)
    {
        var result = new CoolingResult { DirectFanControl = DirectFanControlAvailable() };

        // Raising the power plan is the real, universally-supported lever: it
        // lets Windows and the firmware run the fans harder. Use High for both
        // levels (there is no higher *safe* generic step than this).
        var (code, _) = ProcessHelper.Run("powercfg", $"/setactive {PlanHigh}",
                                          elevated: false, timeoutMs: 15000);
        if (code == 0)
        {
            result.ActionTaken = true;
            result.Message = maximum
                ? "Raised Windows to High performance so the fans are allowed to run flat out. " +
                  "I can't drive the fans directly on this PC - use your motherboard/laptop fan " +
                  "utility for a custom curve, and ease off the workload while it's this hot."
                : "Nudged Windows up to High performance so the fans can ramp up as needed.";
        }
        else
        {
            result.ActionTaken = false;
            result.Message = "I couldn't change the power plan, and I can't reach the fans " +
                "directly on this PC. Open your PC maker's fan utility to raise fan speed.";
        }
        return result;
    }

    /// <summary>
    /// Return to the balanced plan once things have cooled down. Only reports
    /// success when powercfg actually switched.
    /// </summary>
    public bool RelaxCooling()
    {
        var (code, _) = ProcessHelper.Run("powercfg", $"/setactive {PlanBalanced}",
                                          elevated: false, timeoutMs: 15000);
        return code == 0;
    }
}
