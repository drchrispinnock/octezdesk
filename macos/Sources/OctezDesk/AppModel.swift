import AppKit
import Foundation
import OctezDeskCore
import OctezDeskHomebrew

/// Where the wizard was entered. From the dashboard's "Set up baker…" or "Set up DAL node…", Back
/// on that first screen returns to the dashboard.
enum WizardEntry { case welcome, baker, dal }

enum Page: Equatable { case checking, welcome, node, baker, funds, dal, setup, keys, dashboard }

/// Navigation and the wizard's answers, kept while the user goes Back and forth.
@MainActor
final class AppModel: ObservableObject {
    @Published var page: Page = .checking
    @Published private(set) var platform: HomebrewPlatform?

    @Published var node = NodeSettings()
    @Published var dataDir = (NSHomeDirectory() as NSString).appendingPathComponent(".tezos-node")
    /// Nil: don't bake.
    @Published var keys: BakingKeyChoice?
    @Published var baker = BakerSettings()
    @Published var dal = DalSettings()
    @Published var funding = FundingSettings()
    /// Whether the DAL screen has been seen with the current baking choice; if baking changes, it resets to its defaults.
    var dalChosenWhileBaking: Bool?
    private(set) var entry: WizardEntry = .welcome

    @Published var setup: SetupModel?
    @Published var dashboard: DashboardModel?

    let forceWizard = CommandLine.arguments.contains("--wizard")

    func start() async {
        page = .checking
        platform = HomebrewPlatform()
        guard let platform else { page = .welcome; return }
        // Development aid: open a wizard screen directly (node, baker, funds, dal), prefilled from what's installed.
        if let start = ProcessInfo.processInfo.environment["OCTEZDESK_START_PAGE"] {
            await loadInstalled()
            switch start {
            case "node": page = .node
            case "baker": page = .baker
            case "funds": page = .funds
            case "dal": page = .dal
            case "keys": page = .keys
            default: showDashboard()
            }
            return
        }
        let state = await InstallationProbe.probe(platform)
        if state == .ready && !forceWizard { showDashboard() } else { page = .welcome }
    }

    /// Re-checks for Homebrew (after the user installed it).
    func recheckHomebrew() { platform = HomebrewPlatform() }

    // MARK: Wizard

    var baking: Bool { keys != nil }
    var fundsApply: Bool { FundingSettings.applies(to: node.network, keys: keys) }

    func beginWizard() async {
        entry = .welcome
        await loadInstalled()
        page = .node
    }

    func beginBakerSetup() async {
        entry = .baker
        await loadInstalled()
        if keys == nil { keys = BakingKeyChoice(setup: node.network.isMainnet ? .consensusAndCompanion : .masterAndCompanion) }
        page = .baker
    }

    func beginDalSetup() async {
        entry = .dal
        await loadInstalled()
        dal.enabled = true
        if keys?.setup.createsMasterKey == true { dal.attestForMasterKey = true }
        dalChosenWhileBaking = baking
        page = .dal
    }

    /// Prefills the wizard from what's installed, so pressing Next throughout keeps everything as it is.
    func loadInstalled() async {
        guard let platform else { return }
        let nodeService = NodeService(platform)
        if let s = await nodeService.currentSettings() { node = s }
        dataDir = await nodeService.dataDir()
        keys = await BakingKeyService(platform).recordedChoice()
        // Don't bake unless a baker is actually set up.
        if keys != nil {
            let b = await platform.serviceState(.baker)
            if !(await platform.isInstalled(.baker)) || (!b.enabled && !b.active) { keys = nil }
        }
        if let vote = await platform.serviceConfig(.baker)["LQ_VOTE"].flatMap(LiquidityBakingVote.init(rawValue:)) {
            baker = BakerSettings(vote: vote)
        }
        if let d = await DalNodeService(platform).installedSettings() {
            dal = d
            dalChosenWhileBaking = baking
        } else {
            dal = DalSettings()
            dalChosenWhileBaking = nil
        }
        funding = FundingSettings()
    }

    func afterNode() { page = .baker }

    func afterBaker() {
        if dalChosenWhileBaking != baking {
            // Defaults: on when baking, attesting for a master key OctezDesk creates.
            dal = DalSettings(enabled: baking, attestForMasterKey: keys?.setup.createsMasterKey == true)
            dalChosenWhileBaking = baking
        } else {
            dal.attestForMasterKey = keys?.setup.createsMasterKey == true && dal.enabled
        }
        page = fundsApply ? .funds : .dal
    }

    func backFromBaker() { if entry == .baker { showDashboard() } else { page = .node } }
    func backFromDal() {
        if entry == .dal { showDashboard() } else { page = fundsApply ? .funds : .baker }
    }

    func runSetup() {
        guard let platform else { return }
        do {
            let plan = try ProvisioningPlan.make(platform: platform, node: node, dataDir: dataDir, keys: keys,
                                                 dal: dal, baker: baker, funding: fundsApply ? funding : nil)
            let model = SetupModel(plan: plan, app: self)
            setup = model
            page = .setup
            model.run()
        } catch {
            NSAlert.show("Can't set up", error.localizedDescription)
        }
    }

    func setupFinished() {
        if keys != nil { page = .keys } else { showDashboard() }
    }

    /// Leaves setup: to the dashboard if a node is set up, otherwise to Welcome.
    func cancelSetup() async {
        setup?.cancel()
        guard let platform else { page = .welcome; return }
        if await InstallationProbe.probe(platform) == .ready { showDashboard() } else { page = .welcome }
    }

    func showDashboard() {
        guard let platform else { page = .welcome; return }
        if dashboard == nil { dashboard = DashboardModel(platform: platform, app: self) }
        page = .dashboard
        dashboard?.appear()
    }

    func showKeys() { page = .keys }
}

extension NSAlert {
    static func show(_ title: String, _ message: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = message
        a.runModal()
    }

    /// True if the user chose `confirm`.
    static func confirm(_ title: String, _ message: String, confirm: String, destructive: Bool = false) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = message
        let b = a.addButton(withTitle: confirm)
        b.hasDestructiveAction = destructive
        a.addButton(withTitle: "Cancel")
        return a.runModal() == .alertFirstButtonReturn
    }
}
