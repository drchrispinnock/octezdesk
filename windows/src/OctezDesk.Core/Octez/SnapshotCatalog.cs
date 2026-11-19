namespace OctezDesk.Core.Octez;

/// <summary>Information about tzinit snapshots, fetched from the snapshot server.</summary>
public static class SnapshotCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Size in bytes of the snapshot at <paramref name="url"/> (HTTP HEAD), or null if unknown.</summary>
    public static async Task<long?> TryGetSizeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
