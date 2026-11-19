import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Small async HTTP helpers that work with swift-corelibs-foundation too.
public enum HTTP {
    public struct Response: Sendable {
        public var status: Int
        public var body: Data
        public var text: String { String(decoding: body, as: UTF8.self) }
    }

    public static let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 30
        return URLSession(configuration: c)
    }()

    public static func send(_ request: URLRequest) async throws -> Response {
        try await withCheckedThrowingContinuation { cont in
            session.dataTask(with: request) { data, response, error in
                if let error { cont.resume(throwing: error); return }
                cont.resume(returning: Response(status: (response as? HTTPURLResponse)?.statusCode ?? 0, body: data ?? Data()))
            }.resume()
        }
    }

    public static func get(_ url: String, timeout: TimeInterval = 10) async throws -> Response {
        guard let u = URL(string: url) else { throw OctezDeskError("Bad URL: \(url)") }
        var r = URLRequest(url: u)
        r.timeoutInterval = timeout
        return try await send(r)
    }

    public static func head(_ url: String, timeout: TimeInterval = 10) async throws -> (status: Int, length: Int64?) {
        guard let u = URL(string: url) else { throw OctezDeskError("Bad URL: \(url)") }
        var r = URLRequest(url: u)
        r.httpMethod = "HEAD"
        r.timeoutInterval = timeout
        return try await withCheckedThrowingContinuation { cont in
            session.dataTask(with: r) { _, response, error in
                if let error { cont.resume(throwing: error); return }
                let http = response as? HTTPURLResponse
                let len = response?.expectedContentLength ?? -1
                cont.resume(returning: (http?.statusCode ?? 0, len >= 0 ? len : nil))
            }.resume()
        }
    }

    public static func postJSON(_ url: String, _ body: [String: Any], timeout: TimeInterval = 120) async throws -> Response {
        guard let u = URL(string: url) else { throw OctezDeskError("Bad URL: \(url)") }
        var r = URLRequest(url: u)
        r.httpMethod = "POST"
        r.timeoutInterval = timeout
        r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        r.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await send(r)
    }
}

/// The parts of `/delegates/<pkh>` OctezDesk shows.
public struct DelegateInfo: Sendable, Equatable {
    public var companionActive: String?
    public var companionPending: String?
    public var companionPendingCycle: Int?

    public static func parse(_ json: Any) -> DelegateInfo? {
        guard let root = json as? [String: Any] else { return nil } // an error list: not registered
        var info = DelegateInfo()
        if let ck = root["companion_key"] as? [String: Any] {
            info.companionActive = (ck["active"] as? [String: Any])?["pkh"] as? String
            if let last = (ck["pendings"] as? [[String: Any]])?.last {
                info.companionPending = last["pkh"] as? String
                info.companionPendingCycle = last["cycle"] as? Int
            }
        }
        return info
    }
}

public struct RpcError: Error, LocalizedError {
    public var path: String
    public var detail: String
    public var errorDescription: String? { "RPC /\(path) failed: \(detail)" }
}

/// The node RPCs OctezDesk uses, on 127.0.0.1.
public struct NodeRpcClient: Sendable {
    public let port: Int
    public init(port: Int) { self.port = port }

    public func getJSON(_ path: String, timeout: TimeInterval = 3) async throws -> Any {
        let r: HTTP.Response
        do { r = try await HTTP.get("http://127.0.0.1:\(port)/\(path)", timeout: timeout) } catch {
            throw RpcError(path: path, detail: error.localizedDescription)
        }
        guard (200..<300).contains(r.status) else { throw RpcError(path: path, detail: "HTTP \(r.status)") }
        // Mutez amounts come back as bare JSON strings.
        guard let json = try? JSONSerialization.jsonObject(with: r.body, options: [.fragmentsAllowed]) else {
            throw RpcError(path: path, detail: "invalid JSON")
        }
        return json
    }

    public func isBootstrapped() async throws -> (bootstrapped: Bool, syncState: String?) {
        let o = try await getJSON("chains/main/is_bootstrapped") as? [String: Any]
        guard let b = o?["bootstrapped"] as? Bool else { throw RpcError(path: "chains/main/is_bootstrapped", detail: "unexpected reply") }
        return (b, o?["sync_state"] as? String)
    }

    public func peerCount() async throws -> Int {
        guard let a = try await getJSON("network/connections") as? [Any] else { throw RpcError(path: "network/connections", detail: "unexpected reply") }
        return a.count
    }

    public func head() async throws -> (level: Int, timestamp: Date?) {
        guard let o = try await getJSON("chains/main/blocks/head/header") as? [String: Any], let level = o["level"] as? Int else {
            throw RpcError(path: "chains/main/blocks/head/header", detail: "unexpected reply")
        }
        return (level, (o["timestamp"] as? String).flatMap(Self.parseDate))
    }

    public func version() async throws -> String {
        guard let v = (try await getJSON("version") as? [String: Any])?["version"] as? [String: Any],
              let major = v["major"] as? Int, let minor = v["minor"] as? Int else {
            throw RpcError(path: "version", detail: "unexpected reply")
        }
        return "\(major).\(minor)"
    }

    public func chainId() async throws -> String {
        guard let s = try await getJSON("chains/main/chain_id") as? String else { throw RpcError(path: "chains/main/chain_id", detail: "unexpected reply") }
        return s
    }

    public func balance(_ address: String) async throws -> Int64 { try await mutez("chains/main/blocks/head/context/contracts/\(address)/balance") }
    public func stakedBalance(_ address: String) async throws -> Int64 { try await mutez("chains/main/blocks/head/context/contracts/\(address)/staked_balance") }
    public func unstakedFrozenBalance(_ address: String) async throws -> Int64 { try await mutez("chains/main/blocks/head/context/contracts/\(address)/unstaked_frozen_balance") }
    public func unstakedFinalizableBalance(_ address: String) async throws -> Int64 { try await mutez("chains/main/blocks/head/context/contracts/\(address)/unstaked_finalizable_balance") }

    /// The delegate's companion keys; nil if `address` isn't a registered delegate (the node answers with an error).
    public func delegate(_ address: String) async -> DelegateInfo? {
        guard let json = try? await getJSON("chains/main/blocks/head/context/delegates/\(address)") else { return nil }
        return DelegateInfo.parse(json)
    }

    public func isDelegate(_ address: String) async -> Bool { await delegate(address) != nil }

    /// Mutez amounts are JSON strings; null (e.g. no staked balance) counts as 0.
    private func mutez(_ path: String) async throws -> Int64 {
        let json = try await getJSON(path)
        if let s = json as? String { return Int64(s) ?? 0 }
        return 0
    }

    static func parseDate(_ s: String) -> Date? {
        let f = ISO8601DateFormatter()
        if let d = f.date(from: s) { return d }
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: s)
    }
}
