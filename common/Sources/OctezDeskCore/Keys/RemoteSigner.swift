import Foundation

public enum SignerTransport: String, CaseIterable, Sendable, Identifiable {
    case tcp, http, https
    public var id: String { rawValue }

    /// octez-signer's own defaults.
    public var defaultPort: Int {
        switch self {
        case .tcp: 7732
        case .http: 6732
        case .https: 443
        }
    }

    public var displayName: String { rawValue.uppercased() }
}

/// A remote signer (octez-signer, Signatory…) that holds baking keys, so their secrets never sit
/// on this machine. octez-client refers to a key on it as `tcp://host:port/tz4…`.
public struct RemoteSigner: Sendable, Equatable {
    public var transport: SignerTransport
    public var host: String
    public var port: Int

    public init(transport: SignerTransport, host: String, port: Int) {
        self.transport = transport
        self.host = host
        self.port = port
    }

    public var uri: String { "\(transport.rawValue)://\(host):\(port)" }
    public func keyURI(_ address: String) -> String { "\(uri)/\(address)" }

    public func validate() -> [String] {
        var problems: [String] = []
        let h = host.trimmingCharacters(in: .whitespaces)
        if h.isEmpty { problems.append("Enter the remote signer's host name or IP address.") }
        else if !Self.isHostName(h) { problems.append("\"\(h)\" isn't a host name or IP address.") }
        if !(1...65535).contains(port) { problems.append("The signer's port must be between 1 and 65535.") }
        return problems
    }

    static func isHostName(_ h: String) -> Bool {
        if h.hasPrefix("[") && h.hasSuffix("]") {
            return h.dropFirst().dropLast().allSatisfy { $0.isHexDigit || $0 == ":" || $0 == "." }
        }
        guard let first = h.first, let last = h.last, first.isLetter || first.isNumber, last.isLetter || last.isNumber else { return false }
        return h.allSatisfy { ($0.isASCII && ($0.isLetter || $0.isNumber)) || $0 == "." || $0 == "-" }
    }

    /// Reads `uri` back (scheme, host and port; a path is ignored). Nil if it isn't one.
    public static func parse(_ uri: String?) -> RemoteSigner? {
        guard let uri, let c = URLComponents(string: uri), let scheme = c.scheme?.lowercased(),
              let t = SignerTransport(rawValue: scheme), let host = c.host, !host.isEmpty else { return nil }
        let h = host.contains(":") && !host.hasPrefix("[") ? "[\(host)]" : host
        return RemoteSigner(transport: t, host: h, port: c.port ?? t.defaultPort)
    }
}

/// Lists the keys an HTTP(S) remote signer holds (`GET /known_keys`), so the Baker screen can offer them.
/// For a TCP signer the user types the addresses.
public enum RemoteSignerClient {
    public static func listKeys(_ signer: RemoteSigner) async throws -> [String] {
        guard signer.transport != .tcp else {
            throw OctezDeskError("Listing keys over TCP isn't supported yet. Type the key's address instead.")
        }
        let r: HTTP.Response
        do { r = try await HTTP.get(signer.uri + "/known_keys", timeout: 10) } catch {
            throw OctezDeskError("Couldn't reach the signer at \(signer.uri): \(error.localizedDescription)")
        }
        return try knownKeys(fromJSON: r.body, status: r.status, signer: signer)
    }

    public static func knownKeys(fromJSON body: Data, status: Int, signer: RemoteSigner) throws -> [String] {
        let json = try? JSONSerialization.jsonObject(with: body)
        guard (200..<300).contains(status) else {
            let first = (json as? [[String: Any]])?.first
            let reason = (first?["msg"] as? String) ?? (first?["id"] as? String) ?? "HTTP \(status)"
            throw OctezDeskError("The signer at \(signer.uri) won't list its keys: \"\(reason)\". octez-signer lists them only when " +
                                 "started with --allow-list-known-keys. You can type the key's address instead.")
        }
        guard let keys = (json as? [String: Any])?["known_keys"] as? [String] else {
            throw OctezDeskError("The signer at \(signer.uri) sent a reply OctezDesk can't read. Check the protocol and port.")
        }
        return keys
    }
}
