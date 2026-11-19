import Foundation

/// What the user chooses on the DAL node screen.
public struct DalSettings: Sendable, Equatable {
    public static let defaultRpcPort = 10732
    public static let defaultP2PPort = 11732

    public var enabled = false
    /// Attest for the master key created on the Baker screen; its address is resolved during setup.
    public var attestForMasterKey = false
    /// The baker's address to attest for, when it isn't a master key OctezDesk created. Nil: no attester profile.
    public var attesterAddress: String?
    public var rpcPort = defaultRpcPort
    public var p2pPort = defaultP2PPort

    public init(enabled: Bool = false, attestForMasterKey: Bool = false, attesterAddress: String? = nil,
                rpcPort: Int = defaultRpcPort, p2pPort: Int = defaultP2PPort) {
        self.enabled = enabled
        self.attestForMasterKey = attestForMasterKey
        self.attesterAddress = attesterAddress
        self.rpcPort = rpcPort
        self.p2pPort = p2pPort
    }

    /// The DAL node's RPC listens on localhost; the baker is its only client.
    public var rpcAddr: String { "127.0.0.1:\(rpcPort)" }
    public var netAddr: String { "0.0.0.0:\(p2pPort)" }

    public func validate(node: NodeSettings, baking: Bool) -> [String] {
        var errors: [String] = []
        guard enabled else { return errors }
        if !attestForMasterKey {
            let a = attesterAddress?.trimmingCharacters(in: .whitespaces) ?? ""
            if a.isEmpty {
                if baking { errors.append("Enter the address of your baker's master key, so the DAL node attests for it.") }
            } else if !isImplicitAddress(a) {
                errors.append("The attester must be a tz1, tz2, tz3 or tz4 address.")
            }
        }
        for (name, port) in [("DAL RPC port", rpcPort), ("DAL P2P port", p2pPort)] where !NodeSettings.isValidPort(port) {
            errors.append("\(name) must be between 1024 and 65535.")
        }
        let ports = [rpcPort, p2pPort, node.rpcPort, node.p2pPort]
        if Set(ports).count < ports.count { errors.append("The DAL node's ports must differ from each other and from the node's.") }
        return errors
    }

    /// Arguments for `octez-dal-node config init`.
    public func configInitArgs(dataDir: String, nodeRpcPort: Int, attester: String?) -> [String] {
        var args = ["config", "init", "--data-dir", dataDir,
                    "--endpoint", "http://127.0.0.1:\(nodeRpcPort)",
                    "--rpc-addr", rpcAddr, "--net-addr", netAddr]
        if let attester { args += ["--attester-profiles", attester] }
        return args
    }
}

/// What OctezDesk configured the DAL node with, kept in `.octezdesk-dal.json`.
public struct DalSettingsRecord: Codable, Equatable, Sendable {
    public var attester: String?
    public var rpcPort: Int
    public var p2pPort: Int
    public var nodeRpcPort: Int

    enum CodingKeys: String, CodingKey { case attester = "Attester", rpcPort = "RpcPort", p2pPort = "P2PPort", nodeRpcPort = "NodeRpcPort" }

    public init(attester: String?, rpcPort: Int, p2pPort: Int, nodeRpcPort: Int) {
        self.attester = attester
        self.rpcPort = rpcPort
        self.p2pPort = p2pPort
        self.nodeRpcPort = nodeRpcPort
    }

    // Windows writes "Attester": null; keep the key when encoding so the files match.
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(attester, forKey: .attester)
        try c.encode(rpcPort, forKey: .rpcPort)
        try c.encode(p2pPort, forKey: .p2pPort)
        try c.encode(nodeRpcPort, forKey: .nodeRpcPort)
    }
}

/// The baker's vote on the liquidity baking toggle.
public enum LiquidityBakingVote: String, CaseIterable, Sendable, Identifiable {
    case pass, on, off
    public var id: String { rawValue }
}

public struct BakerSettings: Sendable, Equatable {
    public var vote: LiquidityBakingVote = .pass
    public init(vote: LiquidityBakingVote = .pass) { self.vote = vote }
}

/// The baker's settings file, which OctezDesk writes in full. The variables are those of the
/// Homebrew `octez-baker.conf` (a superset of the Debian `/etc/default/octez-baker`).
public enum BakerConfig {
    public static func content(nodeDataDir: String, nodeRpcPort: Int, clientDir: String, keyAliases: [String],
                               dalRpcPort: Int?, settings: BakerSettings) -> String {
        let q = ShellConfigFile.quote
        return [
            ShellConfigFile.managedHeader,
            "MODE=\"local\"",
            "DATADIR=\(q(nodeDataDir))",
            "ENDPOINT=\"http://127.0.0.1:\(nodeRpcPort)\"",
            "CLIENTDIR=\(q(clientDir))",
            "KEYS=\(q(keyAliases.joined(separator: " ")))",
            dalRpcPort.map { "DAL_OPTIONS=\"--dal-node http://127.0.0.1:\($0)\"" } ?? "DAL_OPTIONS=\"--without-dal\"",
            "LQ_VOTE=\"\(settings.vote.rawValue)\"",
            "RUNTIME_OPTS=\"\"",
        ].joined(separator: "\n") + "\n"
    }

    /// `content` set to bake without DAL; nil if it already does (or has no DAL_OPTIONS).
    public static func withoutDal(_ content: String) -> String? {
        let d = ShellConfigFile.parse(content)
        guard let opts = d["DAL_OPTIONS"], opts != "--without-dal" else { return nil }
        return ShellConfigFile.setting("DAL_OPTIONS", to: "--without-dal", in: content)
    }
}

/// The Funds screen: on a test network, when setup creates a master key, get test tez from the faucet and stake.
public struct FundingSettings: Sendable, Equatable {
    /// 6000 ꜩ to stake (the minimum for baking rights) plus 1 ꜩ for fees.
    public static let minimumAmount = 6001
    public static let minimumStake = 6000
    /// Fees paid after funding leave the balance slightly under the amount, so funding counts as done within this.
    public static let feeAllowance = 1

    public var fund = true
    public var amount = minimumAmount
    public var stake = true

    public init(fund: Bool = true, amount: Int = minimumAmount, stake: Bool = true) {
        self.fund = fund
        self.amount = amount
        self.stake = stake
    }

    /// 6000 ꜩ or 10% of the amount, whichever is higher.
    public var stakeAmount: Int { max(Self.minimumStake, amount / 10) }

    public static func applies(to network: TezosNetwork, keys: BakingKeyChoice?) -> Bool {
        network.faucetURL != nil && keys?.setup.createsMasterKey == true
    }

    public func validate(faucetMax: Int?) -> [String] {
        guard fund else { return [] }
        if amount < Self.minimumAmount { return ["Request at least \(Self.minimumAmount.formatted()) ꜩ: \(Self.minimumStake.formatted()) to stake, plus fees."] }
        if let faucetMax, amount > faucetMax { return ["This faucet gives at most \(faucetMax.formatted()) ꜩ per request."] }
        return []
    }
}
