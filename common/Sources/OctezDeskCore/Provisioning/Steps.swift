import Foundation

public final class InstallPackagesStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let packages: [OctezPackage]
    public let title: String

    public init(_ platform: OctezPlatform, _ packages: [OctezPackage], title: String) {
        self.platform = platform
        self.packages = packages
        self.title = title
    }

    public func isDone() async -> Bool {
        for p in packages where !(await platform.isInstalled(p)) { return false }
        return true
    }

    public func run(log: @escaping LogSink) async throws {
        var missing: [OctezPackage] = []
        for p in packages where !(await platform.isInstalled(p)) { missing.append(p) }
        try await platform.install(missing, log: log)
    }
}

/// Points a service at the data directory chosen in the wizard (`DATADIR` in its settings file).
/// Only that line is changed; the rest of the file is kept.
public final class SetDataDirStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let service: OctezService
    let dataDir: String

    public init(_ platform: OctezPlatform, service: OctezService, dataDir: String) {
        self.platform = platform
        self.service = service
        self.dataDir = dataDir
    }

    public var title: String { "Use \(dataDir) for the \(service.displayName)'s data" }

    public func isDone() async -> Bool {
        let current = await platform.serviceConfig(service)["DATADIR"] ?? ""
        let effective = current.isEmpty ? (service == .node ? platform.defaultNodeDataDir : platform.defaultDalDataDir) : current
        return Self.same(effective, dataDir)
    }

    public func run(log: @escaping LogSink) async throws {
        if await platform.serviceState(service).active {
            throw OctezDeskError("The \(service.displayName) is running with another data directory. Stop it first, or keep its current data directory.")
        }
        let path = platform.serviceConfigPath(service)
        let content = await platform.readFile(path) ?? ""
        try await platform.writeFile(path, ShellConfigFile.setting("DATADIR", to: dataDir, in: content))
        log("Set DATADIR in \(path).")
    }

    public static func same(_ a: String, _ b: String) -> Bool {
        (a as NSString).standardizingPath == (b as NSString).standardizingPath
    }
}

/// Runs `octez-node config init` and records the settings used. Done only when the node is
/// configured with these settings. A config OctezDesk made for another network is replaced only if
/// there's no chain data; one OctezDesk didn't make is never replaced.
public final class ConfigureNodeStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let node: NodeService
    let settings: NodeSettings

    public init(_ platform: OctezPlatform, node: NodeService, settings: NodeSettings) {
        self.platform = platform
        self.node = node
        self.settings = settings
    }

    public var title: String { "Configure the node for \(settings.network.displayName) (\(settings.historyMode.cliValue))" }

    public func isDone() async -> Bool {
        guard await node.isConfigured(), await node.recordedSettings() == NodeSettingsRecord(settings) else { return false }
        return await node.rpcListenAddrs() == [settings.rpcAddr]
    }

    public func run(log: @escaping LogSink) async throws {
        let dataDir = await node.dataDir()
        let configFile = await node.configFilePath()
        let wanted = NodeSettingsRecord(settings)

        if let json = await platform.readFile(configFile) {
            if let recorded = await node.recordedSettings() {
                if recorded.isSameChain(wanted) {
                    log("Updating the node's ports and RPC access; its chain data is kept.")
                    try await update(dataDir, log: log)
                    return
                }
            } else {
                // A node OctezDesk didn't set up: use it if it's for the chosen network, never replace it.
                let existing = NodeConfigFile.network(json)
                guard existing == settings.network else {
                    throw OctezDeskError(
                        "\(dataDir) already has a node for \(existing?.displayName ?? "another network") that OctezDesk didn't set up, " +
                        "and OctezDesk won't replace it. Go Back and choose \(existing.map { "\($0.displayName)" } ?? "its network"), " +
                        "or choose a different data directory.")
                }
                if let mode = NodeConfigFile.historyMode(json), mode != settings.historyMode, await node.hasChainData() {
                    throw OctezDeskError("The existing node in \(dataDir) runs in \(mode.cliValue) mode. Go Back and choose \(mode.cliValue), or a different data directory.")
                }
                log("Using the existing \(settings.network.displayName) node in \(dataDir). Its chain data is kept; config.json is backed up to config.json.octezdesk-backup.")
                try await platform.writeFile(configFile + ".octezdesk-backup", json)
                try await update(dataDir, log: log)
                return
            }

            // OctezDesk's own config for another chain. An import still running is for the old settings.
            if await platform.jobState(ImportSnapshotStep.jobName) == .running {
                log("Cancelling the snapshot import for the previous settings.")
                await platform.cancelJob(ImportSnapshotStep.jobName)
            }
            if await node.hasChainData() {
                throw OctezDeskError("The node already has chain data for a different network or history mode. " +
                                     "Go Back and choose the node's current network and history mode to keep it.")
            }
            log("Replacing the existing node configuration (no chain data yet).")
            try await platform.removeFile(configFile)
        }

        try await platform.runOctez("octez-node", settings.configInitArgs(dataDir: dataDir), onLine: log).ensureSuccess("octez-node config init")
        try await node.writeRecord(wanted)
    }

    private func update(_ dataDir: String, log: @escaping LogSink) async throws {
        try await platform.runOctez("octez-node", settings.configUpdateArgs(dataDir: dataDir), onLine: log).ensureSuccess("octez-node config update")
        try await node.writeRecord(NodeSettingsRecord(settings))
        // The node only reads its config at start.
        if await platform.serviceState(.node).active {
            log("Restarting the node.")
            try await platform.restart(.node)
        }
    }
}

/// Downloads and imports a tzinit snapshot, as a job that keeps going if OctezDesk quits. If the
/// job is already running (after OctezDesk was reopened), it waits for it.
public final class ImportSnapshotStep: ProvisioningStep, @unchecked Sendable {
    public static let jobName = "octezdesk-snapshot"
    static let importingMarker = ".octezdesk-importing"

    // $1 URL, $2 data dir, $3 download file, $4 octez-node
    static let script = """
        set -e
        url="$1"; datadir="$2"; file="$3"; node="$4"
        marker="$datadir/\(importingMarker)"
        if [ -f "$marker" ]; then
          echo "Removing an unfinished import."
          rm -rf "$datadir/store" "$datadir/context" "$datadir/lock" "$marker"
        fi
        rm -f "$file"
        echo "Downloading $url"
        curl -fsSL --retry 3 --retry-delay 10 -o "$file" "$url"
        echo "Downloaded. Importing the snapshot; this can take a long time."
        touch "$marker"
        "$node" snapshot import "$file" --data-dir "$datadir"
        rm -f "$marker" "$file"
        echo "Snapshot imported."
        """

    let platform: OctezPlatform
    let node: NodeService
    let settings: NodeSettings

    public init(_ platform: OctezPlatform, node: NodeService, settings: NodeSettings) {
        self.platform = platform
        self.node = node
        self.settings = settings
    }

    public var title: String { "Download and import a \(settings.historyMode.cliValue) snapshot" }

    public func isDone() async -> Bool {
        guard await platform.jobState(Self.jobName) != .running, await node.hasChainData() else { return false }
        return !(await platform.fileExists((await node.dataDir() as NSString).appendingPathComponent(Self.importingMarker)))
    }

    public static func downloadFile(dataDir: String, settings: NodeSettings) -> String {
        let parent = ((dataDir as NSString).standardizingPath as NSString).deletingLastPathComponent
        return (parent as NSString).appendingPathComponent(".octezdesk-snapshot-\(settings.network.id)-\(settings.historyMode.cliValue)")
    }

    public func run(log: @escaping LogSink) async throws {
        let dataDir = await node.dataDir()
        let url = settings.network.snapshotURL(settings.historyMode)
        let file = Self.downloadFile(dataDir: dataDir, settings: settings)

        if await platform.jobState(Self.jobName) == .running {
            log("A snapshot import is already running; waiting for it to finish.")
        } else {
            log("Snapshot: \(url)")
            try await platform.startJob(Self.jobName, script: Self.script,
                                        arguments: [url, dataDir, file, platform.octezToolPath("octez-node")])
        }

        let follower = Task { await platform.follow(platform.jobLogPath(Self.jobName), initialLines: 0, onLine: log) }
        defer { follower.cancel() }
        let size = try? await HTTP.head(url).length
        var lastReport = Date.distantPast
        while true {
            let state = await platform.jobState(Self.jobName)
            if state != .running {
                if case .failed(let why) = state { throw OctezDeskError("The snapshot import failed: \(why). See the log above.") }
                break
            }
            if Date().timeIntervalSince(lastReport) > 30, let attrs = try? FileManager.default.attributesOfItem(atPath: file),
               let have = attrs[.size] as? Int64, have > 0 {
                lastReport = Date()
                log("Downloaded \(Self.formatSize(have))" + (size.map { " of \(Self.formatSize($0))" } ?? ""))
            }
            try await Task.sleep(nanoseconds: 3_000_000_000)
        }
        try? await Task.sleep(nanoseconds: 500_000_000) // let the follower print the last lines
        guard await node.hasChainData() else {
            throw OctezDeskError("The snapshot import finished but the node's store is missing; see the log above.")
        }
    }

    public static func formatSize(_ bytes: Int64) -> String {
        switch bytes {
        case (1 << 30)...: String(format: "%.1f GB", Double(bytes) / Double(1 << 30))
        case (1 << 20)...: "\(bytes >> 20) MB"
        default: "\(bytes >> 10) KB"
        }
    }
}

public final class StartServiceStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let service: OctezService
    public init(_ platform: OctezPlatform, _ service: OctezService) {
        self.platform = platform
        self.service = service
    }

    public var title: String { "Start the \(service.displayName)" }

    public func isDone() async -> Bool {
        let s = await platform.serviceState(service)
        return s.enabled && s.active
    }

    public func run(log: @escaping LogSink) async throws { try await platform.enableAndStart(service) }
}

/// When the wizard turned a service off: stops and disables one set up earlier. Package and data are kept.
public final class DisableServiceStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let service: OctezService
    public init(_ platform: OctezPlatform, _ service: OctezService) {
        self.platform = platform
        self.service = service
    }

    public var title: String { "Turn off the \(service.displayName)" }

    public func isDone() async -> Bool {
        guard await platform.isInstalled(service.package) else { return true }
        let s = await platform.serviceState(service)
        return !s.enabled && !s.active
    }

    public func run(log: @escaping LogSink) async throws { try await platform.disable(service) }
}

/// Creates the keys chosen on the Baker screen, once the node is up (octez-client is slow without one).
public final class GenerateBakingKeysStep: ProvisioningStep, @unchecked Sendable {
    let keys: BakingKeyService
    let choice: BakingKeyChoice
    let node: NodeService

    public init(_ keys: BakingKeyService, choice: BakingKeyChoice, node: NodeService) {
        self.keys = keys
        self.choice = choice
        self.node = node
    }

    public var title: String {
        switch choice.setup {
        case .masterKey: "Generate a \(choice.masterType.rawValue) master key"
        case .masterAndCompanion: "Generate tz4 master and companion keys"
        case .consensusAndCompanion: "Generate tz4 consensus and companion keys"
        case .remoteSigner: "Use the baking keys on the remote signer \(choice.remote?.signer.uri ?? "")"
        }
    }

    public func isDone() async -> Bool { await keys.hasKeys(for: choice) }

    public func run(log: @escaping LogSink) async throws {
        if !(await node.waitForRpc(timeout: 60)) { log("The node's RPC isn't answering yet; going ahead anyway (this is slower).") }
        _ = try await keys.create(choice, log: log)
    }
}

/// Runs `octez-dal-node config init`, pointed at the local node with the attester profile, and
/// records the settings. A changed choice rewrites config.json; the DAL data is kept.
public final class ConfigureDalNodeStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let settings: DalSettings
    let keys: BakingKeyService
    let dal: DalNodeService
    let node: NodeService

    public init(_ platform: OctezPlatform, settings: DalSettings, keys: BakingKeyService) {
        self.platform = platform
        self.settings = settings
        self.keys = keys
        dal = DalNodeService(platform)
        node = NodeService(platform)
    }

    public var title: String { "Configure the DAL node" }

    public func isDone() async -> Bool {
        guard await platform.fileExists(await dal.configFilePath()), let expected = try? await expectedRecord() else { return false }
        return await dal.recordedSettings() == expected
    }

    public func run(log: @escaping LogSink) async throws {
        let record = try await expectedRecord()
        let dataDir = await dal.dataDir()
        let configFile = await dal.configFilePath()
        if let old = await platform.readFile(configFile) {
            if await dal.recordedSettings() == nil {
                log("Backing up the existing DAL node configuration to config.json.octezdesk-backup.")
                try await platform.writeFile(configFile + ".octezdesk-backup", old)
            }
            log("Replacing the DAL node configuration; its data is kept.")
            try await platform.removeFile(configFile)
        }
        log(record.attester.map { "Attester: \($0)" } ?? "No attester profile.")
        try await platform.runOctez("octez-dal-node", settings.configInitArgs(dataDir: dataDir, nodeRpcPort: record.nodeRpcPort, attester: record.attester),
                                    onLine: log).ensureSuccess("octez-dal-node config init")
        try await platform.writeFile(platform.statePath(OctezDeskFiles.dalSettings), RecordJSON.encode(record))
        try await platform.tryRestart(.dalNode) // it only reads its config at start
    }

    private func expectedRecord() async throws -> DalSettingsRecord {
        DalSettingsRecord(attester: try await attester(), rpcPort: settings.rpcPort, p2pPort: settings.p2pPort, nodeRpcPort: await node.rpcPort())
    }

    private func attester() async throws -> String? {
        if !settings.attestForMasterKey {
            let a = settings.attesterAddress?.trimmingCharacters(in: .whitespaces) ?? ""
            return a.isEmpty ? nil : a
        }
        guard let master = await keys.client.showAddress(BakingKeyService.masterAlias) else {
            throw OctezDeskError("The DAL node should attest for the master key '\(BakingKeyService.masterAlias)', but the wallet has no such key.")
        }
        return master.address
    }
}

/// Writes the baker's settings file in full: node, keys, DAL node and liquidity baking vote. A file
/// OctezDesk didn't write is backed up first. Done when the file already has exactly that content.
public final class ConfigureBakerStep: ProvisioningStep, @unchecked Sendable {
    let platform: OctezPlatform
    let keys: BakingKeyService
    let dal: DalSettings?
    let settings: BakerSettings
    let node: NodeService

    public init(_ platform: OctezPlatform, keys: BakingKeyService, dal: DalSettings?, settings: BakerSettings) {
        self.platform = platform
        self.keys = keys
        self.dal = dal
        self.settings = settings
        node = NodeService(platform)
    }

    public var title: String { "Configure the baker (liquidity baking vote: \(settings.vote.rawValue))" }

    public func isDone() async -> Bool {
        guard let expected = try? await expected() else { return false }
        return await platform.readFile(platform.serviceConfigPath(.baker)) == expected
    }

    public func run(log: @escaping LogSink) async throws {
        let content = try await expected()
        let path = platform.serviceConfigPath(.baker)
        if let old = await platform.readFile(path), !ShellConfigFile.isManagedByOctezDesk(old) {
            log("Backing up \(path) to \(path).octezdesk-backup.")
            try await platform.writeFile(path + ".octezdesk-backup", old)
        }
        try await platform.writeFile(path, content)
        log("Wrote \(path):")
        for line in content.split(separator: "\n") { log("  " + line) }
        try await platform.tryRestart(.baker) // it only reads its settings at start
    }

    private func expected() async throws -> String {
        guard let record = await keys.record() else {
            throw OctezDeskError("No baking keys are recorded, so the baker has nothing to bake with.")
        }
        return BakerConfig.content(nodeDataDir: await node.dataDir(), nodeRpcPort: await node.rpcPort(), clientDir: platform.clientDir,
                                   keyAliases: record.orderedAliases, dalRpcPort: dal?.enabled == true ? dal?.rpcPort : nil, settings: settings)
    }
}
