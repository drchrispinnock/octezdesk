import AppKit
import SwiftUI

@main
struct OctezDeskApp: App {
    @StateObject private var model = AppModel()

    init() {
        // Run from `swift run` (no bundle), the app would otherwise have no Dock icon or menu bar.
        NSApplication.shared.setActivationPolicy(.regular)
        DispatchQueue.main.async { NSApplication.shared.activate(ignoringOtherApps: true) }
    }

    var body: some Scene {
        Window("OctezDesk", id: "main") {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 860, minHeight: 620)
                .task { await model.start() }
        }
        .defaultSize(width: 980, height: 720)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }
    }
}

struct RootView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        switch model.page {
        case .checking:
            VStack(spacing: 12) {
                ProgressView()
                Text("Checking the installation…").foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .welcome: WelcomeView()
        case .node: NodeSetupView()
        case .baker: BakerSetupView()
        case .funds: FundsView()
        case .dal: DalSetupView()
        case .setup:
            if let setup = model.setup { SetupProgressView(setup: setup) }
        case .keys: KeysView()
        case .dashboard:
            if let d = model.dashboard { DashboardView(model: d) }
        }
    }
}
