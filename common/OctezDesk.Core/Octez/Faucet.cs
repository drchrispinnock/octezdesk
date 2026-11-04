using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OctezDesk.Core.Octez;

/// <summary>
/// What the user chooses on the Funds screen (between Baker and DAL node): on a test network, when
/// setup creates a master key, get test tez for it from the network's faucet, and stake some.
/// </summary>
public sealed record FundingSettings
{
    /// <summary>6000 ꜩ to stake (the protocol's minimal stake for baking rights), plus 1 ꜩ for fees.</summary>
    public const int MinimumAmount = 6001;
    public const int MinimumStake = 6000;

    /// <summary>
    /// Fees spent after funding (reveal, registration, staking) leave the balance slightly under
    /// the amount received, so funding counts as done within this margin.
    /// </summary>
    public const int FeeAllowance = 1;

    public bool Fund { get; init; } = true;

    /// <summary>ꜩ to have on the master key, requested from the faucet.</summary>
    public int Amount { get; init; } = MinimumAmount;

    /// <summary>Register the master key as a delegate (with its companion key, if any) and stake <see cref="StakeAmount"/>.</summary>
    public bool Stake { get; init; } = true;

    /// <summary>6000 ꜩ or 10% of <see cref="Amount"/>, whichever is higher.</summary>
    public int StakeAmount => Math.Max(MinimumStake, Amount / 10);

    /// <summary>
    /// The Funds screen is offered on test networks that have a faucet, when setup creates a
    /// master key (with a consensus key only, the master key is elsewhere). Mainnet comes later.
    /// </summary>
    public static bool AppliesTo(TezosNetwork network, BakingKeyChoice? keys)
        => network.FaucetUrl is not null && keys?.Setup is BakingKeySetup.MasterKey or BakingKeySetup.MasterAndCompanion;

    /// <param name="faucetMax">The faucet's largest single request, if known.</param>
    public IReadOnlyList<string> Validate(int? faucetMax)
    {
        var errors = new List<string>();
        if (!Fund) return errors;
        if (Amount < MinimumAmount)
            errors.Add($"Request at least {MinimumAmount:N0} ꜩ: {MinimumStake:N0} to stake, plus fees.");
        else if (faucetMax is { } max && Amount > max)
            errors.Add($"This faucet gives at most {max:N0} ꜩ per request.");
        return errors;
    }
}

/// <summary>What a faucet reports at <c>/info</c>.</summary>
public sealed record FaucetInfo(
    [property: JsonPropertyName("challengesEnabled")] bool ChallengesEnabled,
    [property: JsonPropertyName("minTez")] int MinTez,
    [property: JsonPropertyName("maxTez")] int MaxTez,
    [property: JsonPropertyName("maxBalance")] int? MaxBalance);

/// <summary>
/// Client for the teztnets faucets (github.com/tacoinfra/tezos-faucet-backend). Unless challenges
/// are disabled, the faucet hands out a series of proof-of-work challenges, more for larger
/// amounts: find a nonce such that SHA-256 of "challenge:nonce", in hex, starts with
/// <c>difficulty</c> zeros. No captcha is needed; it would only reduce the number of challenges.
/// </summary>
public sealed class Faucet(HttpClient http, Uri baseUrl)
{
    public static Faucet For(TezosNetwork network, HttpClient? http = null)
        => new(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
               new Uri(network.FaucetUrl ?? throw new ArgumentException($"{network.DisplayName} has no faucet.", nameof(network))));

    public Uri BaseUrl => baseUrl;

    public async Task<FaucetInfo> GetInfoAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<FaucetInfo>(new Uri(baseUrl, "info"), ct)
           ?? throw new InvalidOperationException("The faucet returned no information.");

    /// <summary>Requests <paramref name="amount"/> ꜩ for <paramref name="address"/>, solving the challenges. Returns the transfer's operation hash.</summary>
    public async Task<string> RequestAsync(string address, int amount, Action<string> log, CancellationToken ct = default)
    {
        var info = await GetInfoAsync(ct);
        if (!info.ChallengesEnabled)
            return await PostAsync("verify", new { address, amount }, ct) is { TxHash: { } direct }
                ? direct
                : throw new InvalidOperationException("The faucet did not send the tez.");

        var r = await PostAsync("challenge", new { address, amount }, ct);
        while (true)
        {
            if (r.TxHash is { } hash) return hash;
            if (r.Challenge is not { } challenge || r.Difficulty is not { } difficulty)
                throw new InvalidOperationException($"Unexpected answer from the faucet: {r.Message ?? "no challenge"}");

            var counter = r.ChallengeCounter ?? 1;
            var needed = r.ChallengesNeeded ?? counter;
            if (counter == 1 || counter % 10 == 0 || counter == needed)
                log($"Solving the faucet's proof-of-work challenge {counter} of {needed}…");
            var (nonce, solution) = Solve(challenge, difficulty, ct);
            r = await PostAsync("verify", new { address, nonce, solution }, ct);
        }
    }

    /// <summary>Finds a nonce (from 1: the faucet rejects 0) whose hash has <paramref name="difficulty"/> leading hex zeros.</summary>
    public static (long Nonce, string Solution) Solve(string challenge, int difficulty, CancellationToken ct = default)
    {
        var prefix = Encoding.UTF8.GetBytes(challenge + ":");
        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        long found = -1;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = done.Token }, w =>
            {
                var buffer = new byte[prefix.Length + 20];
                prefix.CopyTo(buffer, 0);
                Span<byte> hash = stackalloc byte[32];
                for (long nonce = w + 1; !done.IsCancellationRequested; nonce += workers)
                {
                    nonce.TryFormat(buffer.AsSpan(prefix.Length), out var digits, provider: CultureInfo.InvariantCulture);
                    SHA256.HashData(buffer.AsSpan(0, prefix.Length + digits), hash);
                    if (!HasLeadingHexZeros(hash, difficulty)) continue;
                    Interlocked.CompareExchange(ref found, nonce, -1);
                    done.Cancel();
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();

        var solution = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}:{found}")));
        return (found, solution);
    }

    public static bool HasLeadingHexZeros(ReadOnlySpan<byte> hash, int zeros)
    {
        for (var i = 0; i < zeros / 2; i++)
            if (hash[i] != 0) return false;
        return zeros % 2 == 0 || hash[zeros / 2] < 0x10;
    }

    private async Task<FaucetResponse> PostAsync(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(baseUrl, path), body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        FaucetResponse? r = null;
        try { r = JsonSerializer.Deserialize<FaucetResponse>(text); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode || r is null || r.Status == "ERROR")
            throw new InvalidOperationException(
                $"The faucet refused the request: {r?.Message ?? $"HTTP {(int)response.StatusCode}"}");
        return r;
    }

    private sealed record FaucetResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("txHash")] string? TxHash,
        [property: JsonPropertyName("challenge")] string? Challenge,
        [property: JsonPropertyName("challengeCounter")] int? ChallengeCounter,
        [property: JsonPropertyName("challengesNeeded")] int? ChallengesNeeded,
        [property: JsonPropertyName("difficulty")] int? Difficulty);
}
