import AppKit
import OctezDeskCore
import OctezDeskHomebrew
import SwiftUI

// MARK: 1. Welcome

struct WelcomeView: View {
    @EnvironmentObject var model: AppModel
    @State private var opened = false

    var body: some View {
        VStack(spacing: 20) {
            Spacer()
            if let icon = NSImage(named: "AppIcon") ?? NSApp.applicationIconImage {
                Image(nsImage: icon).resizable().frame(width: 96, height: 96)
            }
            Text("Welcome to OctezDesk").font(.largeTitle).bold()
            Text("OctezDesk sets up a Tezos node on this Mac, and optionally a DAL node and a baker, then keeps an eye on them.")
                .multilineTextAlignment(.center).foregroundStyle(.secondary).frame(maxWidth: 520)

            if let platform = model.platform {
                GroupBox {
                    VStack(alignment: .leading, spacing: 8) {
                        Label("Homebrew found at \(platform.prefix)", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                        Text("Octez is installed from the \(HomebrewPlatform.tap) tap and runs with brew services, as you, " +
                             "whenever you're logged in. Nothing needs an administrator password.")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxWidth: 520, alignment: .leading).padding(6)
                }
                Button("Get started") { Task { await model.beginWizard() } }
                    .keyboardShortcut(.defaultAction).controlSize(.large)
            } else {
                GroupBox {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("Homebrew is needed", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Text("OctezDesk installs Octez with Homebrew, the macOS package manager. Installing Homebrew asks for your " +
                             "password, so it runs in Terminal. When it has finished, come back here and click Check again.")
                            .font(.callout).fixedSize(horizontal: false, vertical: true)
                        Text(HomebrewInstaller.command).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                    }
                    .frame(maxWidth: 520, alignment: .leading).padding(6)
                }
                HStack {
                    Button("Install Homebrew in Terminal…") {
                        do { NSWorkspace.shared.open(try HomebrewInstaller.openInTerminal()); opened = true }
                        catch { NSAlert.show("Couldn't open Terminal", error.localizedDescription) }
                    }
                    Button("Open brew.sh") { NSWorkspace.shared.open(URL(string: "https://brew.sh")!) }
                    Button("Check again") { model.recheckHomebrew() }
                        .keyboardShortcut(opened ? .defaultAction : .init("r", modifiers: .command))
                }
            }
            Spacer()
            if model.dashboard != nil || model.forceWizard {
                Button("Back to the dashboard") { model.showDashboard() }.buttonStyle(.link).padding(.bottom)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: 2. Node

struct NodeSetupView: View {
    @EnvironmentObject var model: AppModel
    @State private var snapshotSize: Int64?
    @State private var freeSpace: Int64?
    @State private var existing: ExistingNode?
    @State private var nodeRunning = false

    struct ExistingNode: Equatable {
        var network: TezosNetwork?
        var hasChainData: Bool
        var ours: Bool
    }

    var problems: [String] {
        var p = model.node.validate()
        if model.dataDir.trimmingCharacters(in: .whitespaces).isEmpty { p.append("Choose a data directory.") }
        if let e = existing, !e.ours, e.network != model.node.network {
            p.append("That directory has a node for \(e.network?.displayName ?? "another network"); choose it or another directory.")
        }
        return p
    }

    var body: some View {
        WizardPage(step: "Step 1 · Node", title: "Your Tezos node",
                   subtitle: "Choose the network and how much history to keep. The node gossips with peers on the gossip port; its RPC listens on this Mac only.",
                   back: { model.page = .welcome }, problems: problems, next: model.afterNode) {
            Section {
                Picker("Network", selection: $model.node.network) {
                    ForEach(TezosNetwork.all) { Text($0.displayName).tag($0) }
                }
                Picker("History mode", selection: $model.node.historyMode) {
                    Text("Rolling: recent blocks only (smallest)").tag(HistoryMode.rolling)
                    Text("Full: all blocks, recent state").tag(HistoryMode.full)
                    Text("Archive: everything (largest)").tag(HistoryMode.archive)
                }
                Toggle(isOn: $model.node.importSnapshot) {
                    VStack(alignment: .leading) {
                        Text("Start from a snapshot")
                        Text(snapshotHint).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .disabled(model.node.historyMode == .archive || existing?.hasChainData == true)
            }
            Section("Data") {
                HStack {
                    TextField("Data directory", text: $model.dataDir).disabled(nodeRunning)
                    Button("Choose…", action: chooseDirectory).disabled(nodeRunning)
                }
                if nodeRunning {
                    Text("The node is running, so it keeps its data directory.").font(.caption).foregroundStyle(.secondary)
                }
                if let freeSpace {
                    Text("\(ImportSnapshotStep.formatSize(freeSpace)) free on this disk. A rolling Mainnet node needs about 50 GB, a full one several hundred.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                if let e = existing { existingNotice(e) }
            }
            Section("Ports") {
                PortField(title: "Gossip (P2P) port", value: $model.node.p2pPort)
                PortField(title: "RPC port", value: $model.node.rpcPort)
                Toggle("Allow all RPC calls (--allow-all-rpc)", isOn: $model.node.allowAllRpc)
            }
        }
        .task(id: model.node.network.id + model.node.historyMode.rawValue) { await refreshSnapshotSize() }
        .task(id: model.dataDir) { await refreshDataDir() }
        .onChange(of: model.node.historyMode) { _, mode in if mode == .archive { model.node.importSnapshot = false } }
    }

    var snapshotHint: String {
        if existing?.hasChainData == true { return "Not needed: this node already has chain data." }
        if model.node.historyMode == .archive { return "Archive nodes sync from the beginning of the chain." }
        let size = snapshotSize.map { " (about \(ImportSnapshotStep.formatSize($0)))" } ?? ""
        return "Downloads the latest snapshot from snapshots.tzinit.org\(size), so the node is ready in hours rather than days."
    }

    @ViewBuilder func existingNotice(_ e: ExistingNode) -> some View {
        if e.ours {
            Label("OctezDesk set up the node here. Its chain data is kept unless you change the network or history mode.",
                  systemImage: "info.circle").font(.callout)
        } else if e.network == model.node.network {
            Label("There's already a \(e.network!.displayName) node here, set up outside OctezDesk. OctezDesk will use it and keep its data " +
                  "(config.json is backed up first).", systemImage: "info.circle").font(.callout)
        } else {
            Label("There's already a node for \(e.network?.displayName ?? "another network") here, set up outside OctezDesk. " +
                  "OctezDesk won't replace it: choose \(e.network?.displayName ?? "its network"), or a different directory.",
                  systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
        }
    }

    func chooseDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use for node data"
        panel.message = "Choose a folder for the node's data. A new folder is best, or one that already holds a node."
        if panel.runModal() == .OK, let url = panel.url { model.dataDir = url.path }
    }

    func refreshSnapshotSize() async {
        snapshotSize = nil
        let url = model.node.network.snapshotURL(model.node.historyMode)
        snapshotSize = (try? await HTTP.head(url))?.length
    }

    func refreshDataDir() async {
        let dir = (model.dataDir as NSString).expandingTildeInPath
        var probe = URL(fileURLWithPath: dir)
        while !FileManager.default.fileExists(atPath: probe.path) && probe.path != "/" { probe.deleteLastPathComponent() }
        let values = try? probe.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        freeSpace = values?.volumeAvailableCapacityForImportantUsage

        guard let platform = model.platform else { return }
        nodeRunning = await platform.serviceState(.node).active
        let config = (dir as NSString).appendingPathComponent("config.json")
        guard let json = await platform.readFile(config) else { existing = nil; return }
        let currentDir = await NodeService(platform).dataDir()
        let ours = await NodeService(platform).recordedSettings() != nil && SetDataDirStep.same(currentDir, dir)
        existing = ExistingNode(network: NodeConfigFile.network(json),
                                hasChainData: await platform.directoryExists((dir as NSString).appendingPathComponent("store")),
                                ours: ours)
        if existing?.hasChainData == true { model.node.importSnapshot = false }
    }
}

// MARK: 3. Baker

struct BakerSetupView: View {
    @EnvironmentObject var model: AppModel
    @State private var remote = RemoteForm()
    @State private var listedKeys: [String] = []
    @State private var listing = false
    @State private var listError: String?

    struct RemoteForm: Equatable {
        var transport = SignerTransport.tcp
        var host = ""
        var port = SignerTransport.tcp.defaultPort
        var consensus = ""
        var companion = ""

        var signer: RemoteSigner { RemoteSigner(transport: transport, host: host.trimmingCharacters(in: .whitespaces), port: port) }
        var keys: RemoteKeys {
            let c = companion.trimmingCharacters(in: .whitespaces)
            return RemoteKeys(signer: signer, consensusAddress: consensus.trimmingCharacters(in: .whitespaces), companionAddress: c.isEmpty ? nil : c)
        }
    }

    enum Option: Hashable { case none, setup(BakingKeySetup) }

    var options: [BakingKeySetup] { BakingKeySetup.allCases.filter { BakingKeyChoice.isAllowed($0, on: model.node.network) } }

    var selection: Binding<Option> {
        Binding(
            get: { model.keys.map { .setup($0.setup) } ?? .none },
            set: { o in
                switch o {
                case .none: model.keys = nil
                case .setup(let s): model.keys = BakingKeyChoice(setup: s, masterType: model.keys?.masterType ?? .tz4,
                                                                 remote: s == .remoteSigner ? remote.keys : nil)
                }
            })
    }

    var problems: [String] {
        guard let k = model.keys else { return [] }
        if !BakingKeyChoice.isAllowed(k.setup, on: model.node.network) { return ["Choose another option: OctezDesk doesn't create a master key on Mainnet."] }
        if k.setup == .remoteSigner { return remote.keys.validate() }
        return []
    }

    var body: some View {
        WizardPage(step: "Step 2 · Baker", title: "Baking",
                   subtitle: "To bake, OctezDesk needs keys to sign blocks and attestations. Existing keys with these names are reused, never overwritten.",
                   back: model.backFromBaker, problems: problems, next: model.afterBaker) {
            Section {
                Picker("Keys", selection: selection) {
                    Text("Don't bake").tag(Option.none)
                    ForEach(options) { Text($0.describe).tag(Option.setup($0)) }
                }
                .pickerStyle(.radioGroup)
                Text(explanation).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if model.keys?.setup == .masterKey {
                    Picker("Key type", selection: Binding(get: { model.keys?.masterType ?? .tz4 },
                                                          set: { model.keys?.masterType = $0 })) {
                        ForEach(KeyType.allCases) { Text($0.describe).tag($0) }
                    }
                }
            }
            if model.keys?.setup == .remoteSigner { remoteSection }
            if model.baking {
                Section("Baker") {
                    Picker("Liquidity baking vote", selection: $model.baker.vote) {
                        Text("Pass").tag(LiquidityBakingVote.pass)
                        Text("On").tag(LiquidityBakingVote.on)
                        Text("Off").tag(LiquidityBakingVote.off)
                    }
                    Label("A Mac that sleeps misses its baking slots. Turn on Keep this Mac awake in the dashboard's menu, and keep it on power.",
                          systemImage: "moon.zzz").font(.callout).foregroundStyle(.secondary)
                }
            }
        }
        .onAppear {
            if let r = model.keys?.remote {
                remote = RemoteForm(transport: r.signer.transport, host: r.signer.host, port: r.signer.port,
                                    consensus: r.consensusAddress, companion: r.companionAddress ?? "")
            }
            if let k = model.keys, !BakingKeyChoice.isAllowed(k.setup, on: model.node.network) {
                model.keys = BakingKeyChoice(setup: .consensusAndCompanion)
            }
        }
        .onChange(of: remote) { _, r in if model.keys?.setup == .remoteSigner { model.keys?.remote = r.keys } }
    }

    var explanation: String {
        switch model.keys?.setup {
        case nil: "Run just a node. You can set up a baker later from the dashboard's menu."
        case .masterKey: "A new master key in octez-client's wallet, unencrypted so the baker runs unattended. Test networks only."
        case .masterAndCompanion: "A tz4 master key and a tz4 companion key for DAL attestations. Test networks only."
        case .consensusAndCompanion: "tz4 consensus and companion keys. Your master key stays in your own wallet or Ledger; OctezDesk shows the commands to register these keys from it."
        case .remoteSigner: "Keys held by a remote signer (octez-signer, Signatory…). The wallet here keeps only a reference, so no secret key is on this Mac."
        }
    }

    @ViewBuilder var remoteSection: some View {
        Section("Remote signer") {
            Picker("Protocol", selection: $remote.transport) {
                ForEach(SignerTransport.allCases) { Text($0.displayName).tag($0) }
            }
            .onChange(of: remote.transport) { old, new in
                if remote.port == old.defaultPort { remote.port = new.defaultPort }
            }
            TextField("Host", text: $remote.host, prompt: Text("192.168.1.20 or signer.example.com"))
            PortField(title: "Port", value: $remote.port)
            if remote.transport != .tcp {
                HStack {
                    Button(listing ? "Listing…" : "List the signer's keys") { Task { await listKeys() } }
                        .disabled(listing || !remote.signer.validate().isEmpty)
                    if let listError { Text(listError).font(.caption).foregroundStyle(.red) }
                }
            }
            addressField("Consensus key", text: $remote.consensus)
            addressField("Companion key (tz4, optional)", text: $remote.companion)
        }
    }

    func addressField(_ title: String, text: Binding<String>) -> some View {
        HStack {
            TextField(title, text: text, prompt: Text("tz…")).font(.system(.body, design: .monospaced))
            if !listedKeys.isEmpty {
                Menu("Pick") { ForEach(listedKeys, id: \.self) { k in Button(k) { text.wrappedValue = k } } }.fixedSize()
            }
        }
    }

    func listKeys() async {
        listing = true
        listError = nil
        defer { listing = false }
        do {
            listedKeys = try await RemoteSignerClient.listKeys(remote.signer)
            if listedKeys.isEmpty { listError = "The signer has no keys." }
        } catch { listError = error.localizedDescription }
    }
}

// MARK: 4. Funds (test networks, master key)

struct FundsView: View {
    @EnvironmentObject var model: AppModel
    @State private var info: FaucetInfo?
    @State private var infoError: String?

    var body: some View {
        WizardPage(step: "Step 3 · Funds", title: "Test tez",
                   subtitle: "A baker needs at least 6,000 ꜩ staked. On \(model.node.network.displayName), OctezDesk can get them from the faucet once the new master key exists, as the last setup step.",
                   back: { model.page = .baker }, problems: model.funding.validate(faucetMax: info?.maxTez), next: { model.page = .dal }) {
            Section {
                Toggle("Get test tez from the faucet", isOn: $model.funding.fund)
                if model.funding.fund {
                    TextField("Amount (ꜩ)", value: $model.funding.amount, format: .number).frame(maxWidth: 260)
                    if let info { Text("This faucet gives up to \(info.maxTez.formatted()) ꜩ per request.").font(.caption).foregroundStyle(.secondary) }
                    if let infoError { Text(infoError).font(.caption).foregroundStyle(.orange) }
                    Toggle("Register as a baker and stake \(model.funding.stakeAmount.formatted()) ꜩ", isOn: $model.funding.stake)
                    Text("The faucet asks for some proof-of-work, which OctezDesk solves; it takes a few minutes.").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .task {
            do { info = try await Faucet.for(model.node.network).info() } catch { infoError = "Couldn't reach the faucet: \(error.localizedDescription)" }
        }
    }
}

// MARK: 5. DAL node

struct DalSetupView: View {
    @EnvironmentObject var model: AppModel

    var problems: [String] { model.dal.validate(node: model.node, baking: model.baking) }

    var body: some View {
        WizardPage(step: "Step 4 · DAL node", title: "Data availability layer",
                   subtitle: "A DAL node lets your baker attest to DAL data and earn the DAL rewards.",
                   back: model.backFromDal, nextTitle: "Set up", problems: problems, next: model.runSetup) {
            Section {
                Toggle("Run a DAL node", isOn: $model.dal.enabled)
                if model.baking && !model.dal.enabled {
                    Label("Without a DAL node your baker misses DAL attestations and their rewards.", systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).font(.callout)
                }
            }
            if model.dal.enabled {
                Section("Attester") {
                    if model.keys?.setup.createsMasterKey == true {
                        Toggle("Attest for the new master key", isOn: $model.dal.attestForMasterKey)
                    }
                    if !model.dal.attestForMasterKey {
                        TextField(model.baking ? "Your baker's address" : "Baker's address (optional)",
                                  text: Binding(get: { model.dal.attesterAddress ?? "" }, set: { model.dal.attesterAddress = $0 }),
                                  prompt: Text("tz…"))
                            .font(.system(.body, design: .monospaced))
                        Text(model.baking ? "The address of the baker's master key, which the DAL node attests for."
                                          : "Leave empty to run the DAL node without an attester profile.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Section("Ports") {
                    PortField(title: "DAL RPC port", value: $model.dal.rpcPort)
                    PortField(title: "DAL P2P port", value: $model.dal.p2pPort)
                }
            }
            Section("Summary") {
                Text(summary).font(.callout).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    var summary: String {
        var parts = ["A \(model.node.network.displayName) node (\(model.node.historyMode.rawValue))" +
                     (model.node.importSnapshot ? ", started from a snapshot" : "") + ", data in \(model.dataDir)."]
        if let k = model.keys {
            parts.append("Baking with \(k.setup.describe.lowercased()); liquidity baking vote \(model.baker.vote.rawValue).")
        } else { parts.append("No baker.") }
        parts.append(model.dal.enabled ? "A DAL node." : "No DAL node.")
        if model.fundsApply && model.funding.fund {
            parts.append("Then \(model.funding.amount.formatted()) ꜩ from the faucet" + (model.funding.stake ? ", registered and staked." : "."))
        }
        return parts.joined(separator: " ")
    }
}
