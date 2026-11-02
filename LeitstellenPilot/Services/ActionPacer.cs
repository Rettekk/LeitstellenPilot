using System;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public sealed class ActionPacer
{
    private readonly ConfigService _config;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public string WaitingReason { get; private set; } = "Aktionspause";
    public event Action<int>? Waiting;
    public event Action<int>? PauseStarted;
    public ActionPacer(ConfigService config, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _config = config; _delay = delay ?? Task.Delay; }

    public Task WaitAsync(CancellationToken token) => WaitAsync(_config.Config.BotSettings.MinActionDelaySeconds,
        _config.Config.BotSettings.MaxActionDelaySeconds, "Aktionspause", token);

    public Task WaitPersonnelAsync(CancellationToken token) => WaitAsync(_config.Config.BotSettings.MinPersonnelActionDelaySeconds,
        _config.Config.BotSettings.MaxPersonnelActionDelaySeconds, "Personalpause", token);

    private async Task WaitAsync(int minimum, int maximum, string reason, CancellationToken token)
    {
        minimum = Math.Clamp(minimum, 1, 300);
        maximum = Math.Clamp(maximum, minimum, 300);
        var seconds = Random.Shared.Next(minimum, maximum + 1);
        WaitingReason = reason;
        PauseStarted?.Invoke(seconds);
        await CountdownAsync(seconds, token);
    }

    private async Task CountdownAsync(int seconds, CancellationToken token)
    {
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            token.ThrowIfCancellationRequested();
            Waiting?.Invoke(remaining);
            await _delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
