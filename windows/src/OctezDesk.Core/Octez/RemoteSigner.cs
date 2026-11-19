using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OctezDesk.Core.Octez;

public enum SignerTransport { Tcp, Http, Https }

public static class SignerTransportExtensions
{
    /// <summary>octez-signer's own defaults: TCP 7732, HTTP 6732, HTTPS 443.</summary>
    public static int DefaultPort(this SignerTransport t) => t switch
    {
        SignerTransport.Tcp => 7732,
        SignerTransport.Http => 6732,
        SignerTransport.Https => 443,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static string Scheme(this SignerTransport t) => t.ToString().ToLowerInvariant();
}

/// <summary>
/// A remote signer (octez-signer, Signatory…) that holds baking keys, so their secrets never enter
/// the distro. octez-client refers to a key on it as <c>tcp://host:port/tz4…</c>.
/// </summary>
public sealed record RemoteSigner(SignerTransport Transport, string Host, int Port)
{
    private static readonly Regex HostName = new(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?)$", RegexOptions.Compiled);

    /// <summary>The signer itself: <c>tcp://192.168.1.20:7732</c>.</summary>
    public string Uri => $"{Transport.Scheme()}://{Host}:{Port}";

    /// <summary>The locator octez-client imports for one key: <c>tcp://192.168.1.20:7732/tz4…</c>.</summary>
    public string KeyUri(string address) => $"{Uri}/{address}";

    public override string ToString() => Uri;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) problems.Add("Enter the remote signer's host name or IP address.");
        else if (!HostName.IsMatch(Host)) problems.Add($"\"{Host}\" isn't a host name or IP address.");
        if (Port is < 1 or > 65535) problems.Add("The signer's port must be between 1 and 65535.");
        return problems;
    }

    /// <summary>Reads <see cref="Uri"/> back (scheme, host and port; a path is ignored). Null if it isn't one.</summary>
    public static RemoteSigner? TryParse(string? uri)
    {
        if (uri is null || !System.Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        if (!Enum.TryParse<SignerTransport>(u.Scheme, ignoreCase: true, out var transport)) return null;
        var port = u.IsDefaultPort && transport != SignerTransport.Https ? transport.DefaultPort() : u.Port;
        return port > 0 ? new RemoteSigner(transport, u.Host, port) : null;
    }
}

/// <summary>Why a remote signer couldn't be asked something, in words for the user.</summary>
public sealed class RemoteSignerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lists the keys a remote signer holds, from Windows, so the Baker screen can offer them before
/// octez-client is installed in the distro. Ported from Signet's RemoteSignerClient
/// (signet-windows, Signet.Core): only the <c>known_keys</c> request is needed here.
/// </summary>
public sealed class RemoteSignerClient(HttpClient? http = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout };

    public async Task<IReadOnlyList<string>> ListKeysAsync(RemoteSigner signer, CancellationToken ct = default)
    {
        try
        {
            return signer.Transport == SignerTransport.Tcp ? await ListOverTcpAsync(signer, ct) : await ListOverHttpAsync(signer, ct);
        }
        catch (SignerWire.RefusedException ex)
        {
            throw new RemoteSignerException(
                $"The signer at {signer} won't list its keys" + (ex.Reason is null ? "." : $": \"{ex.Reason}\".") +
                " octez-signer lists them only when started with --allow-list-known-keys. You can type the key's address instead.", ex);
        }
        catch (SignerWire.MalformedException ex)
        {
            throw new RemoteSignerException($"The signer at {signer} sent a reply OctezDesk can't read. Check the protocol and port.", ex);
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException)
        {
            throw new RemoteSignerException($"Couldn't reach the signer at {signer}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteSignerException($"The signer at {signer} didn't answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private async Task<IReadOnlyList<string>> ListOverHttpAsync(RemoteSigner signer, CancellationToken ct)
    {
        using var reply = await _http.GetAsync(signer.Uri + "/known_keys", ct);
        var body = await reply.Content.ReadAsStringAsync(ct);
        return SignerWire.KnownKeysFromJson(body, (int)reply.StatusCode);
    }

    private static async Task<IReadOnlyList<string>> ListOverTcpAsync(RemoteSigner signer, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(new DnsEndPoint(signer.Host, signer.Port), deadline.Token);
        await socket.SendAsync(SignerWire.Frame(SignerWire.KnownKeysRequest), SocketFlags.None, deadline.Token);
        var header = await ReceiveAsync(socket, 2, deadline.Token);
        var reply = await ReceiveAsync(socket, header[0] << 8 | header[1], deadline.Token);
        return SignerWire.AddressesFromValue(SignerWire.Value(reply));
    }

    private static async Task<byte[]> ReceiveAsync(Socket socket, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        for (var read = 0; read < count;)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct);
            if (n == 0) throw new IOException("the signer closed the connection");
            read += n;
        }
        return buffer;
    }
}

/// <summary>The parts of the remote signer protocol OctezDesk speaks (from Signet's SignerWire).</summary>
public static class SignerWire
{
    public sealed class MalformedException() : Exception("The remote signer's reply could not be read.");

    public sealed class RefusedException(string? reason) : Exception("The remote signer refused" + (reason is null ? "." : $": {reason}"))
    {
        public string? Reason { get; } = reason;
    }

    /// <summary>Base58 prefixes of tz1…tz5, in the order of the signer's key hash tags 0…4.</summary>
    private static readonly byte[][] AddressPrefixes = [[6, 161, 159], [6, 161, 161], [6, 161, 164], [6, 161, 166], [6, 161, 169]];

    public static readonly byte[] KnownKeysRequest = [6];

    public static byte[] Frame(byte[] payload) => [(byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    /// <summary>The value in a TCP reply, or the signer's error.</summary>
    public static byte[] Value(byte[] reply)
    {
        if (reply.Length == 0) throw new MalformedException();
        return reply[0] switch
        {
            0 => reply[1..],
            1 => throw new RefusedException(ErrorReason(reply[1..])),
            _ => throw new MalformedException(),
        };
    }

    /// <summary>
    /// Key hashes, 21 bytes each (tag, then 20 bytes); octez may put a 4-byte length in front,
    /// which can't be mistaken for whole items.
    /// </summary>
    public static IReadOnlyList<string> AddressesFromValue(byte[] value)
    {
        if (value.Length % 21 == 4)
        {
            var length = value.Take(4).Aggregate(0, (a, b) => a << 8 | b);
            if (length != value.Length - 4) throw new MalformedException();
            value = value[4..];
        }
        if (value.Length % 21 != 0) throw new MalformedException();
        var addresses = new List<string>();
        for (var start = 0; start < value.Length; start += 21)
        {
            var tag = value[start];
            if (tag >= AddressPrefixes.Length) throw new MalformedException();
            addresses.Add(Base58CheckEncode(AddressPrefixes[tag], value.AsSpan(start + 1, 20)));
        }
        return addresses;
    }

    /// <summary>An HTTP signer's <c>/known_keys</c> reply: <c>{"known_keys": ["tz…", …]}</c>, or an error list.</summary>
    public static IReadOnlyList<string> KnownKeysFromJson(string body, int status)
    {
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { throw status is >= 200 and < 300 ? new MalformedException() : new RefusedException($"HTTP {status}"); }

        if (status is < 200 or >= 300)
        {
            var first = (json as JsonArray)?.FirstOrDefault() as JsonObject;
            throw new RefusedException(first?["msg"]?.GetValue<string>() ?? first?["id"]?.GetValue<string>() ?? $"HTTP {status}");
        }
        if (json?["known_keys"] is not JsonArray list) throw new MalformedException();
        return list.Select(k => k?.GetValue<string>() ?? throw new MalformedException()).ToList();
    }

    /// <summary>octez writes each error as BSON (kind, id, often msg); the message is the readable part, the id the fallback.</summary>
    private static string? ErrorReason(byte[] bytes) => BsonString("msg", bytes) ?? BsonString("id", bytes);

    private static string? BsonString(string name, byte[] bytes)
    {
        byte[] key = [2, .. Encoding.UTF8.GetBytes(name), 0];
        for (var index = 0; index + key.Length + 4 <= bytes.Length; index++)
        {
            if (!bytes.AsSpan(index, key.Length).SequenceEqual(key)) continue;
            var at = index + key.Length;
            var length = bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;
            var start = at + 4;
            if (length is >= 1 and <= 4096 && start + length <= bytes.Length && bytes[start + length - 1] == 0)
                return Encoding.UTF8.GetString(bytes, start, length - 1);
        }
        return null;
    }

    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static string Base58CheckEncode(byte[] prefix, ReadOnlySpan<byte> payload)
    {
        byte[] body = [.. prefix, .. payload];
        byte[] bytes = [.. body, .. SHA256.HashData(SHA256.HashData(body)).AsSpan(0, 4)];

        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0) zeros++;
        var digits = new List<byte>(); // little-endian base-58 digits
        foreach (var b in bytes.AsSpan(zeros))
        {
            int carry = b;
            for (var i = 0; i < digits.Count; i++)
            {
                carry += digits[i] << 8;
                digits[i] = (byte)(carry % 58);
                carry /= 58;
            }
            for (; carry > 0; carry /= 58) digits.Add((byte)(carry % 58));
        }
        var text = new StringBuilder(zeros + digits.Count).Append('1', zeros);
        for (var i = digits.Count - 1; i >= 0; i--) text.Append(Alphabet[digits[i]]);
        return text.ToString();
    }
}
