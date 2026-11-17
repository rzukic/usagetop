using System.Text.Json;

namespace AiUsage.Data;

/// <summary>
/// Token storage. Every value is sealed before it reaches the disk, by whatever
/// <see cref="ISecretSealer"/> suits the machine: DPAPI in the Windows app, AES
/// under a supplied key on the server.
///
/// An instance speaks for one account of each provider. The one built from a path
/// is the first account; <see cref="For"/> gives another account a view of the
/// same file whose provider settings are kept under that account's own names.
/// Providers, sign-in and renewal take whichever view they are handed and never
/// need to know there are others.
/// </summary>
public sealed class Credentials
{
    private const string KeyKimiChina = "kimi_china";
    private const string KeyMistralBudget = "mistral_budget";
    private const string KeyMistralSpent = "mistral_spent";
    private const string KeyServerUrl = "server_url";
    private const string KeyServerToken = "server_token";
    private const string KeyAccounts = "accounts";

    /// <summary>The file and what is in it, shared by every account's view.</summary>
    private sealed class Vault(string path, ISecretSealer sealer)
    {
        public readonly object Gate = new();
        public readonly string Path = path;
        public readonly ISecretSealer Sealer = sealer;
        public Dictionary<string, string> Values = [];
    }

    private readonly Vault _vault;

    public Credentials(string path, ISecretSealer sealer)
    {
        _vault = new Vault(path, sealer);
        _vault.Values = Load();
        Slot = "";
    }

    private Credentials(Vault vault, string slot)
    {
        _vault = vault;
        Slot = slot;
    }

    /// <summary>Which account this view speaks for; empty for the first.</summary>
    public string Slot { get; }

    /// <summary>The view of another account of the same providers. The empty slot is the first account.</summary>
    public Credentials For(string slot) =>
        slot == Slot ? this : new Credentials(_vault, slot);

    // ---- Per-provider values, in this view's account ------------------------

    public string? Token(ProviderId id) => Get(KeyToken(id))?.Trim() is { Length: > 0 } value
        ? value
        : null;

    public void SetToken(ProviderId id, string? value) => Set(KeyToken(id), value?.Trim());

    public string? RefreshToken(ProviderId id) => Get(KeyRefresh(id)) is { Length: > 0 } value
        ? value
        : null;

    public void SetRefreshToken(ProviderId id, string? value) => Set(KeyRefresh(id), value);

    /// <summary>Epoch millis at which the access token expires; 0 when unknown.</summary>
    public long ExpiresAt(ProviderId id) =>
        long.TryParse(Get(KeyExpires(id)), out var value) ? value : 0L;

    public void SetExpiresAt(ProviderId id, long value) =>
        Set(KeyExpires(id), value > 0L ? value.ToString() : null);

    /// <summary>ChatGPT account id, needed as a header on the usage endpoint.</summary>
    public string? AccountId(ProviderId id) => Get(KeyAccount(id)) is { Length: > 0 } value
        ? value
        : null;

    public void SetAccountId(ProviderId id, string? value) => Set(KeyAccount(id), value);

    /// <summary>True when the provider was set up through the browser flow.</summary>
    public bool IsSignedIn(ProviderId id) => RefreshToken(id) != null;

    /// <summary>
    /// True when the access token belongs to a CLI on this machine rather than to
    /// a sign-in of our own. A borrowed token is read from the CLI's file again on
    /// every refresh and never renewed here: the refresh token behind it is
    /// single-use, so renewing it would sign the CLI out.
    /// </summary>
    public bool Borrowed(ProviderId id) => Get(KeyBorrowed(id)) == "true";

    public void SetBorrowed(ProviderId id, bool value) => Set(KeyBorrowed(id), value ? "true" : null);

    public void SignOut(ProviderId id)
    {
        Update(values =>
        {
            values.Remove(KeyToken(id));
            values.Remove(KeyRefresh(id));
            values.Remove(KeyExpires(id));
            values.Remove(KeyAccount(id));
            values.Remove(KeyBorrowed(id));
        });
    }

    /// <summary>
    /// Stores a whole session in one write. Setting the fields one at a time would
    /// leave a window where the new access token sits beside the old refresh token,
    /// and a crash inside it loses a refresh token that can never be asked for
    /// again.
    /// </summary>
    public void SetSession(
        ProviderId id,
        string access,
        string? refresh,
        long expiresAt,
        string? accountId,
        bool borrowed)
    {
        Update(values =>
        {
            values[KeyToken(id)] = access.Trim();
            Put(values, KeyRefresh(id), refresh);
            Put(values, KeyExpires(id), expiresAt > 0L ? expiresAt.ToString() : null);
            if (accountId != null) values[KeyAccount(id)] = accountId;
            Put(values, KeyBorrowed(id), borrowed ? "true" : null);
        });
    }

    /// <summary>
    /// Stores a token typed or pasted in by hand. Whatever session was there
    /// before is over: leaving its refresh token behind would have the next
    /// renewal quietly write the old account's token back over the new one. An
    /// empty value clears the provider, which is how the field signs you out.
    /// </summary>
    public void SetManualToken(ProviderId id, string? value)
    {
        var trimmed = value?.Trim();
        Update(values =>
        {
            Put(values, KeyToken(id), trimmed);
            values.Remove(KeyRefresh(id));
            values.Remove(KeyExpires(id));
            values.Remove(KeyBorrowed(id));
            if (string.IsNullOrEmpty(trimmed)) values.Remove(KeyAccount(id));
        });
    }

    public bool KimiChina() => Get(Scoped(KeyKimiChina)) == "true";

    public void SetKimiChina(bool value) => Set(Scoped(KeyKimiChina), value ? "true" : "false");

    public double? MistralBudget() =>
        double.TryParse(Get(Scoped(KeyMistralBudget)), out var value) ? value : null;

    public void SetMistralBudget(double? value) =>
        Set(Scoped(KeyMistralBudget), value is > 0.0 ? value.Value.ToString("R") : null);

    public double? MistralSpent() =>
        double.TryParse(Get(Scoped(KeyMistralSpent)), out var value) ? value : null;

    public void SetMistralSpent(double? value) =>
        Set(Scoped(KeyMistralSpent), value is >= 0.0 ? value.Value.ToString("R") : null);

    // ---- The server the app reads from (one per file) ------------------------

    /// <summary>
    /// The address of a usagetop server. While one is set the app reads from it
    /// and asks no provider anything itself.
    /// </summary>
    public string? ServerUrl() => Get(KeyServerUrl) is { Length: > 0 } value ? value : null;

    public string? ServerToken() => Get(KeyServerToken) is { Length: > 0 } value ? value : null;

    /// <summary>Both together, so a half-written pair is never used. A null address turns the server off.</summary>
    public void SetServer(string? url, string? token)
    {
        Update(values =>
        {
            Put(values, KeyServerUrl, url?.Trim());
            Put(values, KeyServerToken, string.IsNullOrWhiteSpace(url) ? null : token?.Trim());
        });
    }

    // ---- Accounts (one list per file) ----------------------------------------

    private sealed class AccountBook
    {
        public int Next { get; set; } = 2;

        /// <summary>Extra accounts as "provider-slot" keys, in the order they were added.</summary>
        public List<string> Extra { get; set; } = [];

        /// <summary>Names by account key; the first accounts can carry one too.</summary>
        public Dictionary<string, string> Names { get; set; } = [];
    }

    /// <summary>
    /// Every account: the first of each provider, which always exists, then any
    /// added since, grouped by provider in catalogue order.
    /// </summary>
    public IReadOnlyList<Account> Accounts()
    {
        var book = ReadBook();
        var accounts = new List<Account>();
        foreach (var info in ProviderCatalog.All)
        {
            accounts.Add(new Account(info.Id, "", book.Names.GetValueOrDefault(info.Key)));
            foreach (var key in book.Extra)
            {
                if (Account.Parse(key) is not { } parsed || parsed.Provider != info.Id) continue;
                accounts.Add(new Account(info.Id, parsed.Slot, book.Names.GetValueOrDefault(key)));
            }
        }
        return accounts;
    }

    public Account? FindAccount(string key) =>
        Accounts().FirstOrDefault(account => account.Key == key);

    /// <summary>
    /// Adds another account of a provider and returns it. Slots are numbered and
    /// never reused, so a removed account's history can never be read as a new
    /// one's.
    /// </summary>
    public Account AddAccount(ProviderId provider, string? name)
    {
        Account? added = null;
        UpdateBook(book =>
        {
            var slot = book.Next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            book.Next++;
            var key = Account.KeyOf(provider, slot);
            book.Extra.Add(key);
            if (Clean(name) is { } named) book.Names[key] = named;
            added = new Account(provider, slot, Clean(name));
        });
        return added!;
    }

    public void RenameAccount(string key, string? name)
    {
        UpdateBook(book =>
        {
            if (Clean(name) is { } named) book.Names[key] = named;
            else book.Names.Remove(key);
        });
    }

    /// <summary>
    /// Forgets an added account and everything stored for it. The first account of
    /// a provider cannot be removed, only signed out.
    /// </summary>
    public bool RemoveAccount(string key)
    {
        if (Account.Parse(key) is not { } parsed || parsed.Slot.Length == 0) return false;

        var removed = false;
        lock (_vault.Gate)
        {
            var view = For(parsed.Slot);
            view.SignOut(parsed.Provider);
            Set(view.Scoped(KeyKimiChina), null);
            Set(view.Scoped(KeyMistralBudget), null);
            Set(view.Scoped(KeyMistralSpent), null);
            UpdateBook(book =>
            {
                removed = book.Extra.Remove(key);
                book.Names.Remove(key);
            });
        }
        return removed;
    }

    private static string? Clean(string? name) =>
        name?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, 40)] : null;

    private AccountBook ReadBook()
    {
        try
        {
            return Get(KeyAccounts) is { } json
                ? JsonSerializer.Deserialize<AccountBook>(json) ?? new AccountBook()
                : new AccountBook();
        }
        catch (JsonException)
        {
            return new AccountBook();
        }
    }

    private void UpdateBook(Action<AccountBook> change)
    {
        lock (_vault.Gate)
        {
            var book = ReadBook();
            change(book);
            Set(KeyAccounts, JsonSerializer.Serialize(book));
        }
    }

    // ---- Keys ----------------------------------------------------------------

    /// <summary>The first account's names are the ones used before accounts existed.</summary>
    private string Scoped(string key) => Slot.Length == 0 ? key : $"{key}-{Slot}";

    private string KeyToken(ProviderId id) => Scoped($"token_{ProviderCatalog.Of(id).Key}");

    private string KeyRefresh(ProviderId id) => Scoped($"refresh_{ProviderCatalog.Of(id).Key}");

    private string KeyExpires(ProviderId id) => Scoped($"expires_{ProviderCatalog.Of(id).Key}");

    private string KeyAccount(ProviderId id) => Scoped($"account_{ProviderCatalog.Of(id).Key}");

    private string KeyBorrowed(ProviderId id) => Scoped($"borrowed_{ProviderCatalog.Of(id).Key}");

    private string? Get(string key)
    {
        lock (_vault.Gate) return _vault.Values.TryGetValue(key, out var value) ? value : null;
    }

    private void Set(string key, string? value) => Update(values => Put(values, key, value));

    private static void Put(Dictionary<string, string> values, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) values.Remove(key);
        else values[key] = value;
    }

    private void Update(Action<Dictionary<string, string>> change)
    {
        lock (_vault.Gate)
        {
            change(_vault.Values);
            Save();
        }
    }

    // ---- Storage ----------------------------------------------------------

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_vault.Path)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(_vault.Path);
            var sealedValues = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (sealedValues == null) return new(StringComparer.Ordinal);

            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, sealedValue) in sealedValues)
            {
                var value = _vault.Sealer.Unseal(sealedValue);
                if (value != null) plain[key] = value;
            }
            return plain;
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var sealedValues = _vault.Values.ToDictionary(pair => pair.Key, pair => _vault.Sealer.Seal(pair.Value));
            AtomicFile.WriteAllText(_vault.Path, JsonSerializer.Serialize(sealedValues));
        }
        catch
        {
            // Losing a write is better than crashing the settings screen.
        }
    }
}
