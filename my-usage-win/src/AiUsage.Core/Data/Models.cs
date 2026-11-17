namespace AiUsage.Data;

public enum ProviderId
{
    Claude,
    ChatGpt,
    Mistral,
    Kimi,
    Muse,
}

public enum UsageState
{
    Ok,
    NotConfigured,
    Error,
}

/// <summary>Everything that is fixed about a provider: its key, its name, its mark.</summary>
public sealed record ProviderInfo(ProviderId Id, string Key, string DisplayName, string Monogram);

public static class ProviderCatalog
{
    public static readonly ProviderInfo Claude = new(ProviderId.Claude, "claude", "Claude", "C");
    public static readonly ProviderInfo ChatGpt = new(ProviderId.ChatGpt, "chatgpt", "ChatGPT", "G");
    public static readonly ProviderInfo Mistral = new(ProviderId.Mistral, "mistral", "Mistral", "M");
    public static readonly ProviderInfo Kimi = new(ProviderId.Kimi, "kimi", "Kimi", "K");
    public static readonly ProviderInfo Muse = new(ProviderId.Muse, "muse", "Muse", "U");

    public static readonly IReadOnlyList<ProviderInfo> All =
        [Claude, ChatGpt, Mistral, Kimi, Muse];

    public static ProviderInfo Of(ProviderId id) => All.First(p => p.Id == id);

    public static ProviderInfo? FromKey(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One subscription: a provider, and which of possibly several accounts with it.
/// The first account of every provider has the empty slot, so its key is just the
/// provider's ("claude") and everything stored before accounts existed is still
/// its own. Another account gets a numbered slot: "claude-2".
/// </summary>
public sealed record Account(ProviderId Provider, string Slot = "", string? Name = null)
{
    public string Key => KeyOf(Provider, Slot);

    public bool IsDefault => Slot.Length == 0;

    /// <summary>"Claude", or "Claude · Work" for a named account.</summary>
    public string DisplayName => DisplayNameOf(Provider, Name);

    public static string KeyOf(ProviderId provider, string slot) =>
        slot.Length == 0 ? ProviderCatalog.Of(provider).Key : $"{ProviderCatalog.Of(provider).Key}-{slot}";

    public static string DisplayNameOf(ProviderId provider, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? ProviderCatalog.Of(provider).DisplayName
            : $"{ProviderCatalog.Of(provider).DisplayName} · {name.Trim()}";

    /// <summary>"claude" or "claude-2" back into its provider and slot; null for anything else.</summary>
    public static (ProviderId Provider, string Slot)? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var dash = key.IndexOf('-');
        var providerKey = dash < 0 ? key : key[..dash];
        var slot = dash < 0 ? "" : key[(dash + 1)..];

        if (ProviderCatalog.FromKey(providerKey) is not { } info) return null;
        if (dash >= 0 && !IsSlot(slot)) return null;
        return (info.Id, slot);
    }

    /// <summary>Digits and lower-case letters only: a slot ends up in keys, paths and column values.</summary>
    public static bool IsSlot(string slot) =>
        slot.Length is > 0 and <= 16 && slot.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z');
}

/// <summary>
/// One quota window for a provider. <see cref="RemainingPercent"/> is how much of
/// the window is still available, 0-100; null for providers that only expose a
/// balance.
/// </summary>
public sealed record UsageWindow(
    string Label,
    float? RemainingPercent = null,
    DateTimeOffset? ResetAt = null,
    string? Detail = null,
    /// <summary>
    /// False when the window belongs on the provider's own card but not in the
    /// combined figure. Claude's Fable window is a slice of its 7-day one, so
    /// counting it would weigh the same usage twice; Mistral's API credit cannot
    /// be spent on coding workflows, so it is not part of what the subscription
    /// gives you.
    /// </summary>
    bool CountsTowardCombined = true);

public sealed record ProviderUsage
{
    public required ProviderId Id { get; init; }
    public UsageState State { get; init; } = UsageState.Ok;
    public IReadOnlyList<UsageWindow> Windows { get; init; } = [];
    public string? Message { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The subscription tier, e.g. "Pro" or "Education".</summary>
    public string? Plan { get; init; }

    /// <summary>The failure says nothing about the quota, so an earlier reading may stand in.</summary>
    public bool Transient { get; init; }

    /// <summary><see cref="Windows"/> come from an earlier fetch because this one failed.</summary>
    public bool Stale { get; init; }

    /// <summary>
    /// The provider answered 401: the access token is no longer accepted. Kept
    /// apart from the message so the fetch can renew the token and try once more
    /// instead of reading its own wording back.
    /// </summary>
    public bool Rejected { get; init; }

    /// <summary>Which of the provider's accounts this is; empty for the first.</summary>
    public string Slot { get; init; } = "";

    /// <summary>What the user called the account, e.g. "Work"; null when unnamed.</summary>
    public string? AccountName { get; init; }

    /// <summary>"claude", or "claude-2" for a second account: what history and the server key it by.</summary>
    public string AccountKey => Account.KeyOf(Id, Slot);

    /// <summary>"Claude", or "Claude · Work".</summary>
    public string DisplayName => Account.DisplayNameOf(Id, AccountName);
}

public sealed record Snapshot(IReadOnlyList<ProviderUsage> Usages, DateTimeOffset FetchedAt);

/// <summary>One reading of a window's remaining level.</summary>
public readonly record struct UsagePoint(DateTimeOffset At, float Percent);
