import AppKit
import OctezDeskCore
import OctezDeskHomebrew
import SwiftUI

/// Runs a provisioning plan and tracks each step's status.
@MainActor
final class SetupModel: ObservableObject {
    let plan: Provisioner
    unowned let app: AppModel
    @Published var statuses: [StepStatus]
    @Published var running = false
    @Published var finished = false
    let log = LogBuffer(limit: 5000)
    private var task: Task<Void, Never>?

    init(plan: Provisioner, app: AppModel) {
        self.plan = plan
        self.app = app
        statuses = Array(repeating: .pending, count: plan.steps.count)
    }

    var failed: Bool { statuses.contains { if case .failed = $0 { true } else { false } } }

    func run() {
        guard !running else { return }
        running = true
        finished = false
        statuses = Array(repeating: .pending, count: plan.steps.count)
        let plan = plan, sink = log.sink
        task = Task {
            let ok = (try? await plan.run(progress: { i, s in Task { @MainActor in self.statuses[i] = s } }, log: sink)) ?? false
            // Let the last progress updates land first.
            try? await Task.sleep(nanoseconds: 300_000_000)
            running = false
            finished = ok
            if ok { sink("Setup finished.") }
        }
    }

    func cancel() {
        task?.cancel()
        running = false
    }
}

struct SetupProgressView: View {
    @EnvironmentObject var app: AppModel
    @ObservedObject var setup: SetupModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("SETTING UP").font(.caption).foregroundStyle(.secondary)
                Text(setup.finished ? "All set" : setup.failed ? "Setup stopped" : "Setting up").font(.largeTitle).bold()
                Text("Each step checks what's already done, so you can retry after a failure, or quit and come back: a snapshot import carries on without OctezDesk.")
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .padding([.horizontal, .top], 28).padding(.bottom, 12)

            HSplitView {
                List(Array(setup.plan.steps.enumerated()), id: \.offset) { i, step in
                    HStack(alignment: .top, spacing: 8) {
                        icon(setup.statuses[i]).frame(width: 18)
                        VStack(alignment: .leading) {
                            Text(step.title)
                            if case .failed(let why) = setup.statuses[i] {
                                Text(why).font(.caption).foregroundStyle(.red).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    .padding(.vertical, 2)
                }
                .frame(minWidth: 280, idealWidth: 340)
                LogView(log: setup.log, placeholder: "Starting…").frame(minWidth: 360)
            }

            Divider()
            HStack {
                Button(setup.running ? "Cancel" : "Close") {
                    if setup.running, !NSAlert.confirm("Stop setting up?", "Steps already done are kept. A snapshot import that has started keeps going.", confirm: "Stop") { return }
                    Task { await app.cancelSetup() }
                }
                Spacer()
                if setup.failed && !setup.running {
                    Button("Back to the wizard") { app.page = .dal }
                    Button("Retry") { setup.run() }.keyboardShortcut(.defaultAction)
                }
                if setup.finished {
                    Button("Continue") { app.setupFinished() }.keyboardShortcut(.defaultAction)
                }
            }
            .padding(16)
        }
    }

    @ViewBuilder func icon(_ s: StepStatus) -> some View {
        switch s {
        case .pending: Image(systemName: "circle").foregroundStyle(.secondary)
        case .checking, .running: ProgressView().controlSize(.small)
        case .alreadyDone: Image(systemName: "checkmark.circle").foregroundStyle(.green)
        case .done: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .failed: Image(systemName: "xmark.octagon.fill").foregroundStyle(.red)
        }
    }
}

// MARK: Keys

struct KeysView: View {
    @EnvironmentObject var app: AppModel
    @State private var keys: [BakingKey]?
    @State private var status: MasterAccountStatus?
    @State private var busy: String?
    @State private var error: String?
    @State private var record: BakingKeysRecord?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("BAKER KEYS").font(.caption).foregroundStyle(.secondary)
                Text("Your baking keys").font(.largeTitle).bold()
                Text("These keys are in octez-client's wallet (\(app.platform?.clientDir ?? "~/.tezos-client")). Only public data is shown.")
                    .foregroundStyle(.secondary)
            }
            .padding([.horizontal, .top], 28).padding(.bottom, 12)

            Form {
                if keys == nil {
                    HStack { ProgressView().controlSize(.small); Text("Reading the keys…") }
                } else if keys!.isEmpty {
                    Text("OctezDesk hasn't set up any baking keys. Use Set up baker… in the dashboard's menu.")
                }
                ForEach(keys ?? []) { k in
                    Section("\(k.role.describe) · wallet alias \"\(k.alias)\"") {
                        CopyableField(title: "Address", value: k.address)
                        CopyableField(title: "Public key", value: k.publicKey)
                        if let pop = k.proofOfPossession, k.role != .master { CopyableField(title: "Proof of possession", value: pop) }
                    }
                }
                if let keys, keys.contains(where: { $0.role == .consensus }) {
                    Section("Next steps") {
                        Text(KeyInstructions.intro).font(.callout).fixedSize(horizontal: false, vertical: true)
                        ForEach(KeyInstructions.steps(keys), id: \.title) { CopyableField(title: $0.title, value: $0.command) }
                    }
                }
                if let keys, keys.contains(where: { $0.role == .master }) { masterSection }
                if let error { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            }
            .formStyle(.grouped)

            Divider()
            HStack {
                Button("Refresh") { Task { await load() } }.disabled(busy != nil)
                Spacer()
                Button("Continue to the dashboard") { app.showDashboard() }.keyboardShortcut(.defaultAction)
            }
            .padding(16)
        }
        .task { await load() }
    }

    @ViewBuilder var masterSection: some View {
        Section("Master key on chain") {
            if let busy { HStack { ProgressView().controlSize(.small); Text(busy) } }
            if let s = status {
                LabeledContent("Spendable", value: "\(TezAmount.format(s.balance)) ꜩ")
                LabeledContent("Staked", value: "\(TezAmount.format(s.staked)) ꜩ")
                if s.unstakedFrozen > 0 { LabeledContent("Unstaked, still frozen", value: "\(TezAmount.format(s.unstakedFrozen)) ꜩ") }
                if s.unstakedFinalizable > 0 {
                    LabeledContent("Ready to withdraw", value: "\(TezAmount.format(s.unstakedFinalizable)) ꜩ")
                    Button("Withdraw") { act("Withdrawing the unstaked tez…") { try await MasterAccount(app.platform!).finalizeUnstake() } }
                }
                if s.isDelegate {
                    Label("Registered as a baker.", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                    if s.staked < Int64(FundingSettings.minimumStake) * TezAmount.mutezPerTez {
                        Text("Stake at least \(FundingSettings.minimumStake.formatted()) ꜩ to get baking rights.").font(.callout)
                        if s.balance > Int64(FundingSettings.minimumStake) * TezAmount.mutezPerTez {
                            Button("Stake \(FundingSettings.minimumStake.formatted()) ꜩ") {
                                act("Staking…") {
                                    let a = MasterAccount(app.platform!)
                                    try await a.client.waitForBootstrapped()
                                    try await a.client.stake(Int64(FundingSettings.minimumStake), for: BakingKeyService.masterAlias)
                                }
                            }
                        }
                    }
                } else if s.balance > 0 {
                    Text("The master key has funds. Register it as a baker" + (hasCompanion ? ", with its companion key." : ".")).font(.callout)
                    Button("Register as baker") {
                        act("Registering as a baker. This waits for the node to be bootstrapped and the operation to be included…") {
                            try await MasterAccount(app.platform!).register(withCompanion: hasCompanion)
                        }
                    }
                } else {
                    Text("Send tez to the master key's address (\(s.address)), then register it as a baker here.").font(.callout)
                        .textSelection(.enabled)
                }
            } else if busy == nil {
                Text("Couldn't read the master key's state from the node. It may still be starting; try Refresh in a moment.").font(.callout)
            }
        }
    }

    var hasCompanion: Bool { keys?.contains { $0.role == .companion } == true }

    func act(_ text: String, _ work: @escaping () async throws -> Void) {
        busy = text
        error = nil
        Task {
            do { try await work() } catch { self.error = error.localizedDescription }
            busy = nil
            await load()
        }
    }

    func load() async {
        guard let platform = app.platform else { return }
        let service = BakingKeyService(platform)
        keys = await service.keys()
        record = await service.record()
        if keys?.contains(where: { $0.role == .master }) == true { status = await MasterAccount(platform).status() }
    }
}
