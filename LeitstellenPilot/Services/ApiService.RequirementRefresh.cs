using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeitstellenPilot.Models;

namespace LeitstellenPilot.Services;

public sealed partial class ApiService
{
    private async Task<MissionDetails> ParseDetailsWithRefreshAsync(string html, Mission mission, CancellationToken token)
    {
        var details = GamePageParser.ParseDetails(html, mission, _references.Snapshot.Aliases);
        if (!_refreshReferences || !details.UnparsedRequirements.Any()) return details;
        // Every mission shares the reference service's five-minute retry budget.
        // Reparse the same page; a catalog refresh never submits a game action.
        await _references.RefreshForUnknownAsync(token).ConfigureAwait(false);
        return GamePageParser.ParseDetails(html, mission, _references.Snapshot.Aliases);
    }
}
