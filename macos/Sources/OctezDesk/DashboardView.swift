import AppKit
import OctezDeskCore
import OctezDeskHomebrew
import SwiftUI

/// Polls the services' health and follows their logs.
@MainActor
final class DashboardModel: ObservableObject {
    let platform: HomebrewPlatform
    unowned let app: AppModel
    let monitor: Monitor

    @Published var node: NodeHealth?
    @Published var dal: ServiceHealth?
    @Published var baker: ServiceHealth?
    @Published var networkLabel = ""
    @Published var octezVersion: String?
    @Published var busy: String?
    @Published var keepAwake = KeepAwake.isOn
    @Published var hasKeys = false
    @Published var tab: OctezService? = .node

    let logs: [OctezService: LogBuffer] = Dictionary(uniqueKeysWithValues: OctezService.allCases.map { ($0, LogBuffer()) })
    let activity = LogBuffer()
    private var poll: Task<Void, Never>?
    private var followers: [OctezService: Task<Void, Never>] = [:]

    init(platform: HomebrewPlatform, app: AppModel) {
        self.platform = platform
        self.app = app
        monitor = Monitor(platform)
    }

    func appear() {
        guard poll == nil else { Task { await refresh() }; return }
        poll = Task {
            while !Task.isCancelled {
                await refresh()
                try? await Task.sleep(nanoseconds: 5_000_000_000)
            }
        }
        for s in OctezService.allCases { followers[s] = follow(s) }
    }

    func refresh() async {
        async let n = monitor.checkNode()
        async let d = monitor.checkDal()
        async let b = monitor.checkBaker()
        node = await n
        dal = await d
        baker = await b
        let settings = await NodeService(platform).currentSettings()
        networkLabel = settings.map { "\($0.network.displayName) · \($0.historyMode.rawValue) · RPC 127.0.0.1:\($0.rpcPort)" } ?? ""
        octezVersion = await platform.installedVersion(.node)
        hasKeys = await BakingKeyService(platform).record() != nil
        keepAwake = KeepAwake.isOn
    }

    /// Follows the service's newest log, switching when a new daily log appears.
    func follow(_ s: OctezService) -> Task<Void, Never> {
        let log = logs[s]!
        let platform = platform
        return Task {
            var current: String?
            var tail: Task<Void, Never>?
            while !Task.isCancelled {
                let path = await platform.logFile(s)
                if path != current {
                    tail?.cancel()
                    current = path
                    if let path {
                        log.append("── \(path)")
                        let sink = log.sink
                        tail = Task { await platform.follow(path, initialLines: 300, onLine: sink) }
                    }
                }
                try? await Task.sleep(nanoseconds: 60_000_000_000)
            }
            tail?.cancel()
        }
    }

    func run(_ text: String, _ work: @escaping (@escaping LogSink) async throws -> Void) {
        guard busy == nil else { return }
        busy = text
        activity.append("== \(text)")
        tab = nil
        let sink = activity.sink
        Task {
            do {
                try await work(sink)
                sink("Done.")
            } catch {
                sink("!! \(error.localizedDescription)")
                NSAlert.show("\(text) failed", error.localizedDescription)
            }
            busy = nil
            await refresh()
        }
    }

    func control(_ s: OctezService, _ a: ServiceControl.Action) {
        if a == .stop {
            let extra = s == .node ? " The DAL node and baker can't work without it." : ""
            guard NSAlert.confirm("Stop the \(s.displayName)?", "It starts again when you log in, or from Start in this menu.\(extra)", confirm: "Stop") else { return }
        }
        run(ServiceControl.describe(s, a) + "…") { [platform] _ in try await ServiceControl.run(platform, s, a) }
    }

    func turnOff(_ s: OctezService) {
        guard NSAlert.confirm("Deconfigure and stop the \(s.displayName)?",
                              "It stops and won't start at login. Its package, data and the keys are kept; Set up \(s.displayName)… turns it on again." +
                              (s == .dalNode ? " The baker switches to baking without DAL." : ""),
                              confirm: "Deconfigure", destructive: true) else { return }
        run("Turning off the \(s.displayName)…") { [platform] _ in try await ServiceControl.turnOff(platform, s) }
    }

    func upgrade() {
        guard NSAlert.confirm("Upgrade Octez?", "OctezDesk runs brew update and brew upgrade for the Octez formulae, then restarts what's running.", confirm: "Upgrade") else { return }
        run("Upgrading Octez") { [platform] log in try await ServiceControl.upgrade(platform, log: log) }
    }

    func setKeepAwake(_ on: Bool) {
        run(on ? "Keeping this Mac awake" : "Letting this Mac sleep") { _ in try await KeepAwake.set(on) }
    }
}

struct DashboardView: View {
    @EnvironmentObject var app: AppModel
    @ObservedObject var model: DashboardModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            VStack(spacing: 10) {
                nodeRow
                serviceRow("DAL node", model.dal, setup: { Task { await app.beginDalSetup() } })
                serviceRow("Baker", model.baker, setup: { Task { await app.beginBakerSetup() } })
            }
            .padding(16)
            Divider()
            logs
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) { menu }
        }
    }

    var header: some View {
        HStack(alignment: .center, spacing: 14) {
            StatusDot(level: model.node?.level, size: 22)
            VStack(alignment: .leading, spacing: 2) {
                Text(model.node?.summary ?? "Checking…").font(.title).bold()
                Text(model.networkLabel).foregroundStyle(.secondary)
            }
            Spacer()
            if let busy = model.busy {
                ProgressView().controlSize(.small)
                Text(busy).foregroundStyle(.secondary)
            }
            if let v = model.octezVersion { Text("Octez \(v)").font(.caption).foregroundStyle(.secondary) }
        }
        .padding(16)
    }

    var nodeRow: some View {
        GroupBox {
            HStack {
                StatusDot(level: model.node?.level)
                Text("Node").bold().frame(width: 80, alignment: .leading)
                Text(nodeDetails).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
            }
        }
    }

    var nodeDetails: String {
        guard let o = model.node?.observation else { return "" }
        var parts: [String] = []
        if let level = o.headLevel { parts.append("Level \(level.formatted())") }
        if let ts = o.headTimestamp { parts.append(ts.formatted(date: .abbreviated, time: .standard)) }
        if let peers = o.peerCount { parts.append("\(peers) peer\(peers == 1 ? "" : "s")") }
        if let v = o.version { parts.append("v\(v)") }
        return parts.joined(separator: " · ")
    }

    func serviceRow(_ name: String, _ h: ServiceHealth?, setup: @escaping () -> Void) -> some View {
        GroupBox {
            HStack {
                StatusDot(level: h?.level)
                Text(name).bold().frame(width: 80, alignment: .leading)
                Text(h?.summary ?? "…").frame(width: 90, alignment: .leading)
                Text(h?.level == nil ? "" : h?.details ?? "").foregroundStyle(.secondary).lineLimit(1).textSelection(.enabled)
                Spacer()
                if h != nil && h?.level == nil { Button("Set up…", action: setup).disabled(model.busy != nil) }
            }
        }
    }

    var logs: some View {
        VStack(spacing: 0) {
            Picker("Log", selection: $model.tab) {
                Text("Node").tag(OctezService?.some(.node))
                Text("DAL node").tag(OctezService?.some(.dalNode))
                Text("Baker").tag(OctezService?.some(.baker))
                Text("OctezDesk").tag(OctezService?.none)
            }
            .pickerStyle(.segmented).labelsHidden().padding(8).frame(maxWidth: 480)
            if let tab = model.tab {
                LogView(log: model.logs[tab]!, placeholder: "No log yet.")
            } else {
                LogView(log: model.activity, placeholder: "Actions you take from the menu are logged here.")
            }
        }
    }

    var menu: some View {
        Menu {
            Button("Upgrade Octez…", action: model.upgrade)
            Divider()
            Menu("Start") { serviceItems { model.control($0, .start) } }
            Menu("Stop") { serviceItems(suffix: "…") { model.control($0, .stop) } }
            Menu("Restart") { serviceItems { model.control($0, .restart) } }
            Menu("Deconfigure and stop") {
                Button("DAL node…") { model.turnOff(.dalNode) }.disabled(model.dal?.level == nil)
                Button("Baker…") { model.turnOff(.baker) }.disabled(model.baker?.level == nil)
            }
            Divider()
            Button("Setup wizard…") { Task { await app.beginWizard() } }
            Button(model.baker?.level == nil ? "Set up baker…" : "Change baker…") { Task { await app.beginBakerSetup() } }
            Button(model.dal?.level == nil ? "Set up DAL node…" : "Change DAL node…") { Task { await app.beginDalSetup() } }
            Button("Baker keys…") { app.showKeys() }.disabled(!model.hasKeys)
            Divider()
            Toggle("Keep this Mac awake", isOn: Binding(get: { model.keepAwake }, set: { model.setKeepAwake($0) }))
            Button("Show node data in Finder") {
                Task { NSWorkspace.shared.open(URL(fileURLWithPath: await NodeService(model.platform).dataDir())) }
            }
            Button("Show settings files in Finder") {
                NSWorkspace.shared.open(URL(fileURLWithPath: (model.platform.serviceConfigPath(.node) as NSString).deletingLastPathComponent))
            }
            Divider()
            Button("Quit OctezDesk (Octez keeps running)") { NSApp.terminate(nil) }
        } label: {
            Label("Menu", systemImage: "line.3.horizontal")
        }
        .disabled(model.busy != nil)
    }

    @ViewBuilder func serviceItems(suffix: String = "", _ action: @escaping (OctezService) -> Void) -> some View {
        Button("Node\(suffix)") { action(.node) }
        Button("DAL node\(suffix)") { action(.dalNode) }.disabled(model.dal?.level == nil)
        Button("Baker\(suffix)") { action(.baker) }.disabled(model.baker?.level == nil)
    }
}
