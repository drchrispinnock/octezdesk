import Foundation

/// What the user chooses on wizard screen 2 (Node).
public struct NodeSettings: Equatable, Sendable {
    public static let defaultP2PPort = 9732
    public static let defaultRpcPort = 8732

    public var network: TezosNetwork = .mainnet
    public var historyMode: HistoryMode = .rolling
    public var p2pPort: Int = defaultP2PPort
    public var rpcPort: Int = defaultRpcPort
    /// Maps to `--allow-all-rpc` on the RPC address.
    public var allowAllRpc = false
    public var importSnapshot = true

    public init(network: TezosNetwork = .mainnet, historyMode: HistoryMode = .rolling,
                p2pPort: Int = defaultP2PPort, rpcPort: Int = defaultRpcPort,
                allowAllRpc: Bool = false, importSnapshot: Bool = true) {
        self.network = network
        self.historyMode = historyMode
        self.p2pPort = p2pPort
        self.rpcPort = rpcPort
        self.allowAllRpc = allowAllRpc
        self.importSnapshot = importSnapshot
    }

    /// Gossip listens on all IPv4 interfaces, so peers can reach it.
    public var netAddr: String { "0.0.0.0:\(p2pPort)" }

    /// The RPC listens on localhost only. It must: on any other address Octez applies its
    /// restrictive default ACL, which blocks `/monitor/heads/main`, and the DAL node exits.
    public var rpcAddr: String { "127.0.0.1:\(rpcPort)" }

    public var rpcArgs: [String] {
        allowAllRpc ? ["--rpc-addr", rpcAddr, "--allow-all-rpc", rpcAddr] : ["--rpc-addr", rpcAddr]
    }

    public func validate() -> [String] {
        var errors: [String] = []
        if !Self.isValidPort(p2pPort) { errors.append("Gossip port must be between 1024 and 65535.") }
        if !Self.isValidPort(rpcPort) { errors.append("RPC port must be between 1024 and 65535.") }
        if p2pPort == rpcPort { errors.append("Gossip and RPC ports must be different.") }
        if importSnapshot && historyMode == .archive {
            errors.append("An archive node cannot be started from a snapshot; it must sync from genesis.")
        }
        return errors
    }

    static func isValidPort(_ p: Int) -> Bool { (1024...65535).contains(p) }

    /// Arguments for `octez-node config update`: sets the ports and RPC access of an existing node, keeping its chain data.
    public func configUpdateArgs(dataDir: String) -> [String] {
        ["config", "update", "--data-dir", dataDir, "--net-addr", netAddr] + rpcArgs
    }

    /// Arguments for `octez-node config init`.
    public func configInitArgs(dataDir: String) -> [String] {
        ["config", "init", "--data-dir", dataDir,
         "--network", network.configValue,
         "--history-mode", historyMode.cliValue,
         "--net-addr", netAddr] + rpcArgs
    }
}

/// What OctezDesk configured the node with, kept in `.octezdesk-node.json`. The JSON is the same
/// as the Windows version's.
public struct NodeSettingsRecord: Codable, Equatable, Sendable {
    public var network: String
    public var historyMode: String
    public var p2pPort: Int
    public var rpcPort: Int
    public var allowAllRpc: Bool

    enum CodingKeys: String, CodingKey {
        case network = "Network", historyMode = "HistoryMode", p2pPort = "P2PPort", rpcPort = "RpcPort", allowAllRpc = "AllowAllRpc"
    }

    public init(network: String, historyMode: String, p2pPort: Int, rpcPort: Int, allowAllRpc: Bool) {
        self.network = network
        self.historyMode = historyMode
        self.p2pPort = p2pPort
        self.rpcPort = rpcPort
        self.allowAllRpc = allowAllRpc
    }

    public init(_ s: NodeSettings) {
        self.init(network: s.network.id, historyMode: s.historyMode.cliValue, p2pPort: s.p2pPort, rpcPort: s.rpcPort, allowAllRpc: s.allowAllRpc)
    }

    /// Same network and history mode: the chain data fits, and only ports or RPC access differ.
    public func isSameChain(_ other: NodeSettingsRecord) -> Bool {
        network == other.network && historyMode == other.historyMode
    }

    /// The wizard settings this record came from, to prefill the Node screen. No snapshot: the node has its data.
    public func toSettings() -> NodeSettings? {
        guard let n = TezosNetwork.withId(network), let m = HistoryMode(rawValue: historyMode) else { return nil }
        return NodeSettings(network: n, historyMode: m, p2pPort: p2pPort, rpcPort: rpcPort, allowAllRpc: allowAllRpc, importSnapshot: false)
    }
}

/// JSON for the records, indented like the Windows version writes them.
public enum RecordJSON {
    public static func encode<T: Encodable>(_ value: T) -> String {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        return (try? String(decoding: e.encode(value), as: UTF8.self)) ?? "{}"
    }

    public static func decode<T: Decodable>(_ type: T.Type, _ text: String?) -> T? {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return try? JSONDecoder().decode(type, from: Data(text.utf8))
    }
}
