import Foundation

/// Tez amounts as octez-client takes them, and as people type them.
public enum TezAmount {
    public static let mutezPerTez: Int64 = 1_000_000

    /// 6000000000 → "6000", 1500000 → "1.5".
    public static func format(_ mutez: Int64) -> String {
        let whole = mutez / mutezPerTez, frac = abs(mutez % mutezPerTez)
        if frac == 0 { return "\(whole)" }
        var f = String(format: "%06lld", frac)
        while f.hasSuffix("0") { f.removeLast() }
        return "\(whole).\(f)"
    }

    /// A positive amount in tez with at most 6 decimals ("1,000.5" is accepted), in mutez; nil if it isn't one.
    public static func parse(_ text: String?) -> Int64? {
        guard var s = text?.trimmingCharacters(in: .whitespaces), !s.isEmpty else { return nil }
        s = s.replacingOccurrences(of: ",", with: "").replacingOccurrences(of: "ꜩ", with: "").trimmingCharacters(in: .whitespaces)
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count <= 2, let whole = Int64(parts[0].isEmpty ? "0" : String(parts[0])), whole >= 0,
              parts[0].allSatisfy(\.isNumber) else { return nil }
        var frac: Int64 = 0
        if parts.count == 2 {
            let f = parts[1]
            guard f.count <= 6, f.allSatisfy(\.isNumber), let n = Int64(f.isEmpty ? "0" : String(f)) else { return nil }
            frac = n * Int64(pow(10.0, Double(6 - f.count)))
        }
        let (m, overflow) = whole.multipliedReportingOverflow(by: mutezPerTez)
        guard !overflow else { return nil }
        let total = m + frac
        return total > 0 ? total : nil
    }
}

/// Tezos implicit account types, by address prefix.
public enum KeyType: String, CaseIterable, Sendable, Identifiable {
    case tz1, tz2, tz3, tz4
    public var id: String { rawValue }

    /// The `octez-client gen keys --sig` value.
    public var sig: String {
        switch self {
        case .tz1: "ed25519"
        case .tz2: "secp256k1"
        case .tz3: "p256"
        case .tz4: "bls"
        }
    }

    public var describe: String {
        switch self {
        case .tz1: "tz1 (Ed25519)"
        case .tz2: "tz2 (secp256k1)"
        case .tz3: "tz3 (P-256)"
        case .tz4: "tz4 (BLS)"
        }
    }

    public static func of(_ address: String) -> KeyType? { allCases.first { address.hasPrefix($0.rawValue) } }
}

/// An implicit account address: tz1 to tz4 and 33 base58 characters.
public func isImplicitAddress(_ s: String?) -> Bool {
    guard let s, s.count == 36, s.hasPrefix("tz"), let d = s.dropFirst(2).first, "1234".contains(d) else { return false }
    let alphabet = Set("123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz")
    return s.dropFirst(3).allSatisfy { alphabet.contains($0) }
}

/// What the user chose on the Baker screen.
public enum BakingKeySetup: String, CaseIterable, Sendable, Identifiable {
    /// A master key of the chosen type, which also signs blocks and attestations.
    case masterKey = "MasterKey"
    /// A tz4 master key plus a tz4 companion key for DAL attestations.
    case masterAndCompanion = "MasterAndCompanion"
    /// tz4 consensus and companion keys only; the master key stays in the user's own wallet.
    case consensusAndCompanion = "ConsensusAndCompanion"
    /// A consensus key, and optionally a companion key, held by a remote signer.
    case remoteSigner = "RemoteSigner"

    public var id: String { rawValue }

    public var createsMasterKey: Bool { self == .masterKey || self == .masterAndCompanion }

    public var describe: String {
        switch self {
        case .masterKey: "Master key"
        case .masterAndCompanion: "Master and companion keys (tz4)"
        case .consensusAndCompanion: "Consensus and companion keys (tz4)"
        case .remoteSigner: "Keys on a remote signer"
        }
    }
}

public enum KeyRole: String, CaseIterable, Sendable, Comparable {
    case master = "Master", consensus = "Consensus", companion = "Companion"

    public static func < (a: KeyRole, b: KeyRole) -> Bool {
        allCases.firstIndex(of: a)! < allCases.firstIndex(of: b)!
    }

    public var describe: String {
        switch self {
        case .master: "Master key"
        case .consensus: "Consensus key"
        case .companion: "Companion key"
        }
    }
}

/// The keys on a remote signer to bake with.
public struct RemoteKeys: Sendable, Equatable {
    public var signer: RemoteSigner
    public var consensusAddress: String
    public var companionAddress: String?

    public init(signer: RemoteSigner, consensusAddress: String, companionAddress: String?) {
        self.signer = signer
        self.consensusAddress = consensusAddress
        self.companionAddress = companionAddress
    }

    public func validate() -> [String] {
        var problems = signer.validate()
        if !isImplicitAddress(consensusAddress) { problems.append("Choose the signer's key to bake with (a tz1, tz2, tz3 or tz4 address).") }
        if let companion = companionAddress {
            if !isImplicitAddress(companion) || KeyType.of(companion) != .tz4 { problems.append("The companion key must be a tz4 address.") }
            else if companion == consensusAddress { problems.append("The companion key must be a different key from the consensus key.") }
            else if KeyType.of(consensusAddress) != .tz4 { problems.append("A companion key is only used with a tz4 consensus key.") }
        }
        return problems
    }
}

/// The Baker screen's answer.
public struct BakingKeyChoice: Sendable, Equatable {
    public var setup: BakingKeySetup
    public var masterType: KeyType
    public var remote: RemoteKeys?

    public init(setup: BakingKeySetup, masterType: KeyType = .tz4, remote: RemoteKeys? = nil) {
        self.setup = setup
        self.masterType = masterType
        self.remote = remote
    }

    /// On mainnet only consensus and companion keys (or keys on a remote signer) are set up: the
    /// master key holds real funds, so it belongs in the user's own wallet or a Ledger.
    public static func isAllowed(_ setup: BakingKeySetup, on network: TezosNetwork) -> Bool {
        !network.isMainnet || setup == .consensusAndCompanion || setup == .remoteSigner
    }
}

/// A key OctezDesk set up. `proofOfPossession` is for tz4 keys, needed to register them on chain.
public struct BakingKey: Sendable, Equatable, Identifiable {
    public var role: KeyRole
    public var alias: String
    public var address: String
    public var publicKey: String
    public var proofOfPossession: String?
    public var alreadyExisted: Bool
    public var id: String { alias }
}

/// Which keys OctezDesk set up, kept in `.octezdesk-keys.json`; aliases and public data only.
/// Same JSON as the Windows version.
public struct BakingKeysRecord: Codable, Equatable, Sendable {
    public var setup: String
    public var aliases: [String: String]
    public var signer: String?

    enum CodingKeys: String, CodingKey { case setup = "Setup", aliases = "Aliases", signer = "Signer" }

    public init(setup: BakingKeySetup, keys: [BakingKey], signer: String? = nil) {
        self.setup = setup.rawValue
        self.aliases = Dictionary(uniqueKeysWithValues: keys.map { ($0.role.rawValue, $0.alias) })
        self.signer = signer
    }

    /// Aliases ordered consensus or master key first, then companion: the order the baker takes them.
    public var orderedAliases: [String] {
        aliases.compactMap { k, v in KeyRole(rawValue: k).map { ($0, v) } }.sorted { $0.0 < $1.0 }.map(\.1)
    }
}

/// The commands to run from the wallet that holds the master key, when OctezDesk made only
/// consensus and companion keys. Shared so every platform shows the same instructions.
public enum KeyInstructions {
    public static let intro =
        "Your baker's master key stays in your own wallet (octez-client, Signet or a Ledger). Its account needs at least " +
        "6,000 ꜩ to stake, plus a little for fees. From that wallet, replacing <baker> with your baker's address, either " +
        "register a new baker with the first command, or set the keys on an existing one. Wallets other than octez-client " +
        "ask for the public keys and proofs of possession shown above. The keys take effect a few cycles later; until then " +
        "the baker here has nothing to sign."

    public static func steps(_ keys: [BakingKey]) -> [(title: String, command: String)] {
        guard let consensus = keys.first(where: { $0.role == .consensus }) else { return [] }
        let companion = keys.first { $0.role == .companion }
        func pop(_ option: String, _ k: BakingKey) -> String { k.proofOfPossession.map { " \(option) \($0)" } ?? "" }
        let consensusArgs = "--consensus-key \(consensus.publicKey)\(pop("--consensus-key-pop", consensus))"
        let companionArgs = companion.map { " --companion-key \($0.publicKey)\(pop("--companion-key-pop", $0))" } ?? ""
        var steps: [(String, String)] = [
            ("New baker", "octez-client register key <baker> as delegate \(consensusArgs)\(companionArgs) --initial-stake \(FundingSettings.minimumStake)"),
            ("Already a baker: consensus key", "octez-client set consensus key for <baker> to \(consensus.publicKey)\(pop("--consensus-key-pop", consensus))"),
        ]
        if let companion {
            steps.append(("Already a baker: companion key", "octez-client set companion key for <baker> to \(companion.publicKey)\(pop("--companion-key-pop", companion))"))
        }
        steps.append(("Stake, if needed", "octez-client stake \(FundingSettings.minimumStake) for <baker>"))
        return steps
    }
}
