using System.Text.Json;

namespace AiUsage.Data;

/// <summary>
/// The cached snapshot, on disk. Explicit DTOs rather than serialising the model
/// directly: the file outlives any one build, so its shape should only change when
/// someone means it to.
/// </summary>
public static class SnapshotCodec
{
    private sealed record WindowDto(
        string Label,
        float? RemainingPercent,
        DateTimeOffset? ResetAt,
        string? Detail,
        bool CountsTowardCombined);

    private sealed record UsageDto(
        string Id,
        string State,
        List<WindowDto> Windows,
        string? Message,
        DateTimeOffset FetchedAt,
        string? Plan,
        bool Transient,
        bool Stale,
        // Absent from caches written before accounts existed, which read as the first account.
        string? Slot = null,
        string? AccountName = null);

    private sealed record SnapshotDto(DateTimeOffset FetchedAt, List<UsageDto> Usages);

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string Encode(Snapshot snapshot)
    {
        var dto = new SnapshotDto(
            snapshot.FetchedAt,
            snapshot.Usages.Select(u => new UsageDto(
                ProviderCatalog.Of(u.Id).Key,
                u.State.ToString(),
                u.Windows.Select(w => new WindowDto(
                    w.Label,
                    w.RemainingPercent,
                    w.ResetAt,
                    w.Detail,
                    w.CountsTowardCombined)).ToList(),
                u.Message,
                u.FetchedAt,
                u.Plan,
                u.Transient,
                u.Stale,
                u.Slot.Length == 0 ? null : u.Slot,
                u.AccountName)).ToList());

        return JsonSerializer.Serialize(dto, Options);
    }

    public static Snapshot? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<SnapshotDto>(json, Options);
            if (dto == null) return null;

            var usages = new List<ProviderUsage>(dto.Usages.Count);
            foreach (var usage in dto.Usages)
            {
                var info = ProviderCatalog.FromKey(usage.Id);
                if (info == null) continue;

                usages.Add(new ProviderUsage
                {
                    Id = info.Id,
                    State = Enum.TryParse<UsageState>(usage.State, out var state)
                        ? state
                        : UsageState.Error,
                    Windows = usage.Windows
                        .Select(w => new UsageWindow(
                            w.Label,
                            w.RemainingPercent,
                            w.ResetAt,
                            w.Detail,
                            w.CountsTowardCombined))
                        .ToList(),
                    Message = usage.Message,
                    FetchedAt = usage.FetchedAt,
                    Plan = usage.Plan,
                    Transient = usage.Transient,
                    Stale = usage.Stale,
                    Slot = usage.Slot is { } slot && Account.IsSlot(slot) ? slot : "",
                    AccountName = usage.AccountName,
                });
            }

            return new Snapshot(usages, dto.FetchedAt);
        }
        catch
        {
            return null;
        }
    }
}
