import Foundation

/// The node: its data directory (from the service settings file), config and records.
public final class NodeService: @unchecked Sendable {
    public let platform: OctezPlatform
    public init(_ platform: OctezPlatform) { self.platform = platform }

    public func dataDir() async -> String {
        let dir = await platform.serviceConfig(.node)["DATADIR"] ?? ""
        return dir.isEmpty ? platform.defaultNodeDataDir : dir
    }

    public func configFilePath() async -> String { (await dataDir() as NSString).appendingPathComponent("config.json") }

    public func isInstalled() async -> Bool { await platform.isInstalled(.node) }
    public func isConfigured() async -> Bool { await platform.fileExists(await configFilePath()) }

    /// True once the node has a store (from a snapshot import or from running).
    public func hasChainData() async -> Bool {
        await platform.directoryExists((await dataDir() as NSString).appendingPathComponent("store"))
    }

    public func configJSON() async -> String? { await platform.readFile(await configFilePath()) }

    public func recordedSettings() async -> NodeSettingsRecord? {
        RecordJSON.decode(NodeSettingsRecord.self, await platform.readFile(platform.statePath(OctezDeskFiles.nodeSettings)))
    }

    public func writeRecord(_ r: NodeSettingsRecord) async throws {
        try await platform.writeFile(platform.statePath(OctezDeskFiles.nodeSettings), RecordJSON.encode(r))
    }

    public func rpcPort() async -> Int {
        (await configJSON()).flatMap(NodeConfigFile.rpcPort) ?? NodeSettings.defaultRpcPort
    }

    public func rpc() async -> NodeRpcClient { NodeRpcClient(port: await rpcPort()) }

    public func rpcListenAddrs() async -> [String]? { (await configJSON()).flatMap(NodeConfigFile.rpcListenAddrs) }

    /// Waits until the node answers `/version`. False if it doesn't within `timeout` seconds.
    public func waitForRpc(timeout: TimeInterval) async -> Bool {
        let rpc = await rpc()
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            if (try? await rpc.version()) != nil { return true }
            if Date() >= deadline || Task.isCancelled { return false }
            try? await Task.sleep(nanoseconds: 2_000_000_000)
        }
    }

    /// The node's settings, for the dashboard header and to prefill the wizard: OctezDesk's record
    /// if it has one, otherwise what `config.json` says (a node set up outside OctezDesk).
    public func currentSettings() async -> NodeSettings? {
        if let s = await recordedSettings()?.toSettings() { return s }
        guard let json = await configJSON(), let network = NodeConfigFile.network(json) else { return nil }
        return NodeSettings(network: network, historyMode: NodeConfigFile.historyMode(json) ?? .full,
                            p2pPort: NodeSettings.defaultP2PPort, rpcPort: NodeConfigFile.rpcPort(json) ?? NodeSettings.defaultRpcPort,
                            importSnapshot: false)
    }
}

/// The DAL node: data directory and OctezDesk's record of its settings.
public final class DalNodeService: @unchecked Sendable {
    public let platform: OctezPlatform
    public init(_ platform: OctezPlatform) { self.platform = platform }

    public func dataDir() async -> String {
        let dir = await platform.serviceConfig(.dalNode)["DATADIR"] ?? ""
        return dir.isEmpty ? platform.defaultDalDataDir : dir
    }

    public func configFilePath() async -> String { (await dataDir() as NSString).appendingPathComponent("config.json") }

    public func recordedSettings() async -> DalSettingsRecord? {
        RecordJSON.decode(DalSettingsRecord.self, await platform.readFile(platform.statePath(OctezDeskFiles.dalSettings)))
    }

    /// The installed DAL settings, to prefill the wizard so running it again doesn't turn the DAL node off; nil if OctezDesk never configured one.
    public func installedSettings() async -> DalSettings? {
        guard await platform.isInstalled(.dalNode), let r = await recordedSettings() else { return nil }
        let state = await platform.serviceState(.dalNode)
        return DalSettings(enabled: state.enabled || state.active, attesterAddress: r.attester, rpcPort: r.rpcPort, p2pPort: r.p2pPort)
    }
}

/// Starts, stops and turns off services from the dashboard menu.
public enum ServiceControl {
    public enum Action: Sendable { case start, stop, restart }

    public static func describe(_ s: OctezService, _ a: Action) -> String {
        let verb = switch a { case .start: "Starting"; case .stop: "Stopping"; case .restart: "Restarting" }
        return "\(verb) the \(s.displayName)"
    }

    public static func run(_ platform: OctezPlatform, _ s: OctezService, _ a: Action) async throws {
        switch a {
        case .start: try await platform.start(s)
        case .stop: try await platform.stop(s)
        case .restart: try await platform.restart(s)
        }
    }

    /// "Deconfigure and stop": stops the DAL node or baker and stops it starting by itself, so the
    /// dashboard shows it as not set up. Package, data and keys are kept. Turning off the DAL node
    /// also switches the baker to bake without DAL.
    public static func turnOff(_ platform: OctezPlatform, _ s: OctezService) async throws {
        guard s != .node else { throw OctezDeskError("The node can't be turned off this way; stop it instead.") }
        try await platform.disable(s)
        if s == .dalNode, let content = await platform.readFile(platform.serviceConfigPath(.baker)),
           let updated = BakerConfig.withoutDal(content) {
            try await platform.writeFile(platform.serviceConfigPath(.baker), updated)
            try await platform.tryRestart(.baker)
        }
    }

    /// Upgrades the installed Octez packages and restarts what's running.
    public static func upgrade(_ platform: OctezPlatform, log: @escaping LogSink) async throws {
        try await platform.upgrade(log: log)
        for s in OctezService.allCases where await platform.serviceState(s).active {
            log("Restarting the \(s.displayName)…")
            try await platform.restart(s)
        }
    }
}

/// Health checks for the dashboard.
public final class Monitor: @unchecked Sendable {
    let platform: OctezPlatform
    let node: NodeService
    let dal: DalNodeService

    public init(_ platform: OctezPlatform) {
        self.platform = platform
        node = NodeService(platform)
        dal = DalNodeService(platform)
    }

    public func checkNode() async -> NodeHealth {
        let state = await platform.serviceState(.node)
        guard state.active else { return HealthEvaluator.evaluate(NodeObservation(serviceActive: false)) }
        let rpc = await node.rpc()
        guard let (bootstrapped, sync) = try? await rpc.isBootstrapped() else {
            return HealthEvaluator.evaluate(NodeObservation(serviceActive: true, rpcReachable: false))
        }
        var o = NodeObservation(serviceActive: true, rpcReachable: true, bootstrapped: bootstrapped, syncState: sync)
        o.peerCount = try? await rpc.peerCount()
        o.version = try? await rpc.version()
        if let (level, ts) = try? await rpc.head() { o.headLevel = level; o.headTimestamp = ts }
        return HealthEvaluator.evaluate(o)
    }

    public func checkDal() async -> ServiceHealth {
        guard await platform.isInstalled(.dalNode) else { return .evaluate(installed: false, enabled: false, active: false, details: "") }
        let s = await platform.serviceState(.dalNode)
        return .evaluate(installed: true, enabled: s.enabled, active: s.active, details: ServiceHealth.describeDal(await dal.recordedSettings()))
    }

    public func checkBaker() async -> ServiceHealth {
        guard await platform.isInstalled(.baker) else { return .evaluate(installed: false, enabled: false, active: false, details: "") }
        let s = await platform.serviceState(.baker)
        return .evaluate(installed: true, enabled: s.enabled, active: s.active,
                         details: ServiceHealth.describeBaker(await platform.readFile(platform.serviceConfigPath(.baker))))
    }
}

/// What to show at startup.
public enum InstallationState: Sendable, Equatable {
    /// The platform's prerequisite (Homebrew on macOS) is missing.
    case prerequisiteMissing
    case notSetUp
    /// A node is installed, configured and starts by itself: show the dashboard.
    case ready
}

public enum InstallationProbe {
    /// Decided from the machine itself, not from OctezDesk's own settings, so reinstalling OctezDesk
    /// recovers cleanly, and a node set up by hand with the same packages is picked up.
    public static func probe(_ platform: OctezPlatform) async -> InstallationState {
        let node = NodeService(platform)
        guard await node.isInstalled(), await node.isConfigured() else { return .notSetUp }
        // The service is only enabled by the last node step, so "enabled" means setup finished
        // (being configured isn't enough: a snapshot import may have failed after config init).
        let s = await platform.serviceState(.node)
        return s.enabled || s.active ? .ready : .notSetUp
    }
}
