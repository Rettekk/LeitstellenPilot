using System;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed class WorkRestPolicy
{
    private DateTime _workStarted;
    private DateTime? _cycleRestUntil;
    private bool _nightResting;
    public DateTime? RestUntil { get; private set; }
    public string RestReason { get; private set; } = "Ruhepause";
    public void Start(DateTime now) { _workStarted = now; _cycleRestUntil = RestUntil = null; _nightResting = false; RestReason = "Ruhepause"; }
    public bool CanRun(BotSettings settings, DateTime now, TimeZoneInfo? timeZone = null)
    {
        if (settings.NightRestEnabled && !NightRestPolicy.HasValidWindow(settings))
        {
            _nightResting = true; _cycleRestUntil = RestUntil = null;
            RestReason = "Nachtruhe · Uhrzeiten prüfen";
            return false;
        }
        var nightUntil = NightRestPolicy.RestUntil(settings, now, timeZone ?? TimeZoneInfo.Local);
        if (nightUntil.HasValue)
        {
            // Scheduled sleep replaces the current cycle pause. It never counts as
            // work and waking starts a fresh work period without stacking pauses.
            _nightResting = true;
            _cycleRestUntil = null;
            RestUntil = nightUntil;
            RestReason = "Nachtruhe";
            return false;
        }
        if (_nightResting) Start(now);
        if (!settings.WorkRestCycleEnabled) { Start(now); return true; }
        if (_cycleRestUntil.HasValue)
        {
            if (now < _cycleRestUntil.Value) { RestUntil = _cycleRestUntil; RestReason = "Ruhepause"; return false; }
            Start(now);
            return true;
        }
        var workHours = double.IsFinite(settings.WorkHours) ? Math.Clamp(settings.WorkHours, 0.25, 24) : 4;
        var restHours = double.IsFinite(settings.RestHours) ? Math.Clamp(settings.RestHours, 0.25, 24) : 1;
        if (now - _workStarted < TimeSpan.FromHours(workHours)) return true;
        _cycleRestUntil = RestUntil = now.AddHours(restHours);
        RestReason = "Ruhepause";
        return false;
    }
}
