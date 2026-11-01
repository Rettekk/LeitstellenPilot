using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private const string TaskRewardsPagePath = "/tasks/index";
    private DateTime _pendingTaskRewardsUntil = DateTime.MinValue;

    public async Task<TaskRewardsResult> CollectTaskRewardsAsync(CancellationToken token = default)
    {
        await _dispatchGate.WaitAsync(token);
        try
        {
            var result = await CollectTaskRewardsCoreAsync(token);
            Logger?.Write(result.State is TaskRewardsState.Unknown or TaskRewardsState.Pending ? LogLevel.Warning : LogLevel.Info,
                "Tasks", "result", result.Message);
            return result;
        }
        finally { _dispatchGate.Release(); }
    }

    private async Task<TaskRewardsResult> CollectTaskRewardsCoreAsync(CancellationToken token)
    {
        EnsureLoggedIn();
        bool Allowed() => _config.Config.BotSettings.AutoClaimTaskRewards && AutomaticActionsAllowed?.Invoke() != false;
        if (!Allowed()) return new(0, "Aufgabenabholung pausiert / ausgeschaltet.", TaskRewardsState.Paused);
        if (_pendingTaskRewardsUntil > UtcNow)
            return new(0, "Letzte Aufgabenabholung wartet auf Bestätigung; keine erneute Sendung.", TaskRewardsState.Pending);
        var page = TaskRewardsPlanner.Read(await GetTextAsync(TaskRewardsPagePath, token), _httpClient.BaseAddress!);
        if (!page.Recognized) return new(0, "Aufgabenformular nicht eindeutig erkannt; im Spiel prüfen.", TaskRewardsState.Unknown);
        if (page.ClaimableIds.Count == 0) return new(0, "Keine fertigen Aufgaben-/Eventbelohnungen verfügbar.", TaskRewardsState.NotAvailable);
        foreach (var field in page.Fields.Where(field => field.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
        await _pacer.WaitAsync(token);
        if (!Allowed()) return new(0, "Aufgabenabholung pausiert / ausgeschaltet.", TaskRewardsState.Paused);
        // Completion, manual collection and CSRF can change during the normal action pause.
        page = TaskRewardsPlanner.Read(await GetTextAsync(TaskRewardsPagePath, token), _httpClient.BaseAddress!);
        if (!page.Recognized) return new(0, "Aufgabenformular während der Pause geändert; keine Abholung gesendet.", TaskRewardsState.Unknown);
        if (page.ClaimableIds.Count == 0) return new(0, "Fertige Aufgaben wurden inzwischen bereits abgeholt.", TaskRewardsState.NotAvailable);
        foreach (var field in page.Fields.Where(field => field.Key == "authenticity_token")) Logger?.RegisterSecret(field.Value);
        try
        {
            _pendingTaskRewardsUntil = UtcNow.AddMinutes(5);
            using var request = new HttpRequestMessage(HttpMethod.Post, page.ActionPath)
            { Content = new FormUrlEncodedContent(page.Fields) };
            request.Headers.Referrer = new Uri(_httpClient.BaseAddress!, TaskRewardsPagePath);
            using var response = await SendAsync(request, token, automaticAction: true, actionAllowed: () => _config.Config.BotSettings.AutoClaimTaskRewards);
            CheckAuthentication(response);
            if ((int)response.StatusCode is not (200 or 302 or 303))
                return new(0, $"Aufgabenabholung abgelehnt (HTTP {(int)response.StatusCode}).", TaskRewardsState.Unknown);
            var after = TaskRewardsPlanner.Read(await GetTextAsync(TaskRewardsPagePath, token), _httpClient.BaseAddress!);
            if (!after.Recognized) return new(0, "Aufgabenabholung gesendet; Bestätigung nicht lesbar.", TaskRewardsState.Pending);
            var claimed = page.ClaimableIds.Count(id => !after.ClaimableIds.Contains(id));
            if (claimed == 0) return new(0, "Aufgabenabholung gesendet; Bestätigung noch ausstehend.", TaskRewardsState.Pending);
            _pendingTaskRewardsUntil = DateTime.MinValue;
            return new(claimed, $"{claimed} Aufgaben-/Eventbelohnungen abgeholt." +
                (after.ClaimableIds.Count > 0 ? " Weitere Belohnungen werden bei der nächsten Aufgabenprüfung berücksichtigt." : ""), TaskRewardsState.Claimed);
        }
        catch (AutomaticActionPausedException)
        {
            _pendingTaskRewardsUntil = DateTime.MinValue;
            return new(0, "Automatik pausiert; Aufgabenabholung wurde nicht gesendet.", TaskRewardsState.Paused);
        }
    }
}
