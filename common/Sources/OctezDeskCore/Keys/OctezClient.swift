import Foundation

/// Runs `octez-client` against the service user's wallet (the one the baker uses) and the local node.
public final class OctezClient: @unchecked Sendable {
    let platform: OctezPlatform
    let node: NodeService

    public init(_ platform: OctezPlatform) {
        self.platform = platform
        node = NodeService(platform)
    }

    func run(_ args: [String], log: LogSink? = nil) async -> CommandResult {
        let port = await node.rpcPort()
        return await platform.runOctez("octez-client", ["-d", platform.clientDir, "-E", "http://127.0.0.1:\(port)"] + args, onLine: log)
    }

    /// Waits until the local node is bootstrapped, so operations are injected on the current chain.
    public func waitForBootstrapped(log: LogSink? = nil) async throws {
        try await run(["bootstrapped"], log: log).ensureSuccess("Waiting for the node to be bootstrapped")
    }

    public func registerDelegate(_ alias: String, companion: String?, log: LogSink? = nil) async throws {
        var args = ["register", "key", alias, "as", "delegate"]
        if let companion { args += ["--companion-key", companion] }
        try await run(args, log: log).ensureSuccess("Registering '\(alias)' as a delegate")
    }

    public func stake(_ tez: Int64, for alias: String, log: LogSink? = nil) async throws {
        try await run(["stake", "\(tez)", "for", alias], log: log).ensureSuccess("Staking \(tez) ꜩ for '\(alias)'")
    }

    /// Unstakes `mutez`, or everything if nil.
    public func unstake(_ mutez: Int64?, for alias: String, log: LogSink? = nil) async throws {
        try await run(["unstake", mutez.map(TezAmount.format) ?? "everything", "for", alias], log: log).ensureSuccess("Unstaking for '\(alias)'")
    }

    public func finalizeUnstake(for alias: String, log: LogSink? = nil) async throws {
        try await run(["finalize", "unstake", "for", alias], log: log).ensureSuccess("Finalizing the unstake for '\(alias)'")
    }

    /// The address and public key under `alias`, or nil if there's none.
    public func showAddress(_ alias: String) async -> (address: String, publicKey: String)? {
        let r = await run(["show", "address", alias])
        guard r.success else { return nil }
        var hash: String?, pk: String?
        for line in r.stdout.split(separator: "\n") {
            let t = line.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("Hash:") { hash = t.dropFirst(5).trimmingCharacters(in: .whitespaces) }
            if t.hasPrefix("Public Key:") { pk = t.dropFirst(11).trimmingCharacters(in: .whitespaces) }
        }
        guard let hash, let pk, !hash.isEmpty, !pk.isEmpty else { return nil }
        return (hash, pk)
    }

    /// Generates an unencrypted key, so the baker can use it unattended. Fails if the alias exists.
    public func generateKeys(_ alias: String, _ type: KeyType) async throws {
        try await run(["gen", "keys", alias, "--sig", type.sig]).ensureSuccess("Generating key '\(alias)'")
    }

    /// Adds a key held by a remote signer: the wallet stores only the locator.
    public func importRemoteKey(_ alias: String, uri: String) async throws {
        try await run(["import", "secret", "key", alias, uri]).ensureSuccess("Importing '\(alias)' from the remote signer")
    }

    public func createBlsProof(_ alias: String) async throws -> String {
        let r = try await run(["create", "bls", "proof", "for", alias]).ensureSuccess("Creating a proof of possession for '\(alias)'")
        if let line = r.stdout.split(separator: "\n").map({ $0.trimmingCharacters(in: .whitespaces) }).first(where: { $0.hasPrefix("BLsig") }) {
            return line
        }
        throw OctezDeskError("Unexpected output from create bls proof: \(r.stdout.trimmingCharacters(in: .whitespacesAndNewlines))")
    }
}

/// Creates the keys for a `BakingKeySetup` and records which ones OctezDesk set up.
/// Existing keys are reused, never overwritten: they may hold funds.
public final class BakingKeyService: @unchecked Sendable {
    public static let masterAlias = "baker"
    public static let consensusAlias = "consensus"
    public static let companionAlias = "companion"

    let platform: OctezPlatform
    public let client: OctezClient

    public init(_ platform: OctezPlatform) {
        self.platform = platform
        client = OctezClient(platform)
    }

    public static func plan(_ setup: BakingKeySetup, masterType: KeyType) -> [(role: KeyRole, alias: String, type: KeyType)] {
        switch setup {
        case .masterKey: [(.master, masterAlias, masterType)]
        case .masterAndCompanion: [(.master, masterAlias, .tz4), (.companion, companionAlias, .tz4)]
        case .consensusAndCompanion: [(.consensus, consensusAlias, .tz4), (.companion, companionAlias, .tz4)]
        case .remoteSigner: []
        }
    }

    public static func plan(_ remote: RemoteKeys) -> [(role: KeyRole, alias: String, address: String)] {
        var p: [(KeyRole, String, String)] = [(.consensus, consensusAlias, remote.consensusAddress)]
        if let c = remote.companionAddress { p.append((.companion, companionAlias, c)) }
        return p
    }

    public func record() async -> BakingKeysRecord? {
        RecordJSON.decode(BakingKeysRecord.self, await platform.readFile(platform.statePath(OctezDeskFiles.keys)))
    }

    private func writeRecord(_ r: BakingKeysRecord) async throws {
        try await platform.writeFile(platform.statePath(OctezDeskFiles.keys), RecordJSON.encode(r))
    }

    public func create(_ choice: BakingKeyChoice, log: LogSink? = nil) async throws -> [BakingKey] {
        if choice.setup == .remoteSigner {
            guard let remote = choice.remote else { throw OctezDeskError("No remote signer keys were chosen.") }
            return try await importRemote(remote, log: log)
        }
        let plan = Self.plan(choice.setup, masterType: choice.masterType)

        // Check every existing alias first, so a type mismatch fails before anything is generated.
        var existing: [String: (address: String, publicKey: String)] = [:]
        for (_, alias, type) in plan {
            guard let key = await client.showAddress(alias) else { continue }
            if KeyType.of(key.address) != type {
                throw OctezDeskError("The wallet already has a key called '\(alias)' (\(key.address)), but this setup needs a \(type.rawValue) key. " +
                                     "OctezDesk never overwrites keys; rename or remove the existing one with octez-client first.")
            }
            existing[alias] = key
        }

        var keys: [BakingKey] = []
        for (role, alias, type) in plan {
            var found = existing[alias]
            let reused = found != nil
            if let found { log?("Using the existing key '\(alias)' (\(found.address)).") } else {
                log?("Generating \(type.describe) key '\(alias)'…")
                try await client.generateKeys(alias, type)
                found = await client.showAddress(alias)
                guard found != nil else { throw OctezDeskError("Key '\(alias)' was generated but cannot be read back.") }
            }
            let pop = type == .tz4 ? try await client.createBlsProof(alias) : nil
            keys.append(BakingKey(role: role, alias: alias, address: found!.address, publicKey: found!.publicKey,
                                  proofOfPossession: pop, alreadyExisted: reused))
        }
        try await writeRecord(BakingKeysRecord(setup: choice.setup, keys: keys))
        return keys
    }

    private func importRemote(_ remote: RemoteKeys, log: LogSink?) async throws -> [BakingKey] {
        let problems = remote.validate()
        if !problems.isEmpty { throw OctezDeskError(problems.joined(separator: " ")) }
        let plan = Self.plan(remote)
        var existing: [String: (address: String, publicKey: String)] = [:]
        for (_, alias, address) in plan {
            guard let key = await client.showAddress(alias) else { continue }
            if key.address != address {
                throw OctezDeskError("The wallet already has a key called '\(alias)' (\(key.address)), not \(address). " +
                                     "OctezDesk never overwrites keys; rename or remove the existing one with octez-client first.")
            }
            existing[alias] = key
        }
        var keys: [BakingKey] = []
        for (role, alias, address) in plan {
            var found = existing[alias]
            let reused = found != nil
            if reused { log?("Using the existing key '\(alias)' (\(address)).") } else {
                log?("Importing '\(alias)' (\(address)) from the remote signer at \(remote.signer.uri)…")
                try await client.importRemoteKey(alias, uri: remote.signer.keyURI(address))
                found = await client.showAddress(alias)
                guard found != nil else { throw OctezDeskError("Key '\(alias)' was imported but cannot be read back.") }
            }
            keys.append(BakingKey(role: role, alias: alias, address: found!.address, publicKey: found!.publicKey,
                                  proofOfPossession: await tryBlsProof(alias, address: address, log: log), alreadyExisted: reused))
        }
        try await writeRecord(BakingKeysRecord(setup: .remoteSigner, keys: keys, signer: remote.signer.uri))
        return keys
    }

    /// A tz4 key's proof of possession, or nil: a remote signer may refuse to make one.
    private func tryBlsProof(_ alias: String, address: String, log: LogSink?) async -> String? {
        guard KeyType.of(address) == .tz4 else { return nil }
        do { return try await client.createBlsProof(alias) } catch {
            log?("No proof of possession for '\(alias)': \(error.localizedDescription)")
            return nil
        }
    }

    /// The choice OctezDesk recorded, to prefill the wizard; nil if none.
    public func recordedChoice() async -> BakingKeyChoice? {
        guard let r = await record(), let setup = BakingKeySetup(rawValue: r.setup) else { return nil }
        if setup == .remoteSigner {
            guard let signer = RemoteSigner.parse(r.signer), let ca = r.aliases[KeyRole.consensus.rawValue],
                  let consensus = await client.showAddress(ca) else { return nil }
            var companion: String?
            if let a = r.aliases[KeyRole.companion.rawValue] { companion = await client.showAddress(a)?.address }
            return BakingKeyChoice(setup: setup, remote: RemoteKeys(signer: signer, consensusAddress: consensus.address, companionAddress: companion))
        }
        var masterType = KeyType.tz4
        if setup == .masterKey, let m = await client.showAddress(Self.masterAlias) { masterType = KeyType.of(m.address) ?? .tz4 }
        return BakingKeyChoice(setup: setup, masterType: masterType)
    }

    /// True if the keys for `choice` are recorded and all in the wallet with the right types.
    public func hasKeys(for choice: BakingKeyChoice) async -> Bool {
        guard let r = await record(), r.setup == choice.setup.rawValue else { return false }
        if let remote = choice.remote {
            guard r.signer == remote.signer.uri else { return false }
            let plan = Self.plan(remote)
            guard r.aliases.count == plan.count else { return false }
            for (_, alias, address) in plan where await client.showAddress(alias)?.address != address { return false }
            return true
        }
        for (_, alias, type) in Self.plan(choice.setup, masterType: choice.masterType) {
            guard let key = await client.showAddress(alias), KeyType.of(key.address) == type else { return false }
        }
        return true
    }

    /// The keys OctezDesk set up, read back from the wallet with fresh proofs of possession. Missing ones are left out.
    public func keys() async -> [BakingKey] {
        guard let r = await record() else { return [] }
        var keys: [BakingKey] = []
        for (roleName, alias) in r.aliases {
            guard let role = KeyRole(rawValue: roleName), let key = await client.showAddress(alias) else { continue }
            let pop: String? = KeyType.of(key.address) == .tz4 ? await tryBlsProof(alias, address: key.address, log: nil) : nil
            keys.append(BakingKey(role: role, alias: alias, address: key.address, publicKey: key.publicKey, proofOfPossession: pop, alreadyExisted: true))
        }
        return keys.sorted { $0.role < $1.role }
    }
}
