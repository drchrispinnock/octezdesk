import Foundation

public enum HistoryMode: String, CaseIterable, Sendable, Codable, Identifiable {
    case rolling, full, archive
    public var id: String { rawValue }
    public var cliValue: String { rawValue }
}

/// A Tezos network OctezDesk can set a node up for.
public struct TezosNetwork: Hashable, Sendable, Identifiable {
    /// Short name, also used in snapshot and faucet URLs.
    public let id: String
    public let displayName: String
    /// The value for `octez-node config init --network`: a built-in alias or a teztnets URL.
    public let configValue: String

    public static let mainnet = TezosNetwork(id: "mainnet", displayName: "Mainnet", configValue: "mainnet")
    public static let shadownet = TezosNetwork(id: "shadownet", displayName: "Shadownet", configValue: "https://teztnets.com/shadownet")
    public static let bakingnet = TezosNetwork(id: "bakingnet", displayName: "Bakingnet", configValue: "https://teztnets.com/bakingnet")
    public static let ushuaianet = TezosNetwork(id: "ushuaianet", displayName: "Ushuaianet", configValue: "https://teztnets.com/ushuaianet")

    public static let all: [TezosNetwork] = [.mainnet, .shadownet, .bakingnet, .ushuaianet]

    public static func withId(_ id: String) -> TezosNetwork? { all.first { $0.id == id } }

    public var isMainnet: Bool { self == .mainnet }

    /// The tzinit snapshot, e.g. https://snapshots.tzinit.org/shadownet/rolling.
    public func snapshotURL(_ mode: HistoryMode) -> String {
        "https://snapshots.tzinit.org/\(id)/\(mode.cliValue)"
    }

    /// The teztnets faucet, e.g. https://faucet.bakingnet.teztnets.com; nil on mainnet.
    public var faucetURL: String? { isMainnet ? nil : "https://faucet.\(id).teztnets.com" }
}
