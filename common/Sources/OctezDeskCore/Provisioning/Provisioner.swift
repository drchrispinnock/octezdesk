import Foundation

/// One idempotent setup step. `isDone` inspects the real state of the machine, so an interrupted or
/// failed run can simply be run again and resumes at the first unfinished step.
public protocol ProvisioningStep: AnyObject, Sendable {
    var title: String { get }
    func isDone() async -> Bool
    func run(log: @escaping LogSink) async throws
}

public enum StepStatus: Sendable, Equatable {
    case pending, checking, running, alreadyDone, done
    case failed(String)
}

public final class Provisioner: Sendable {
    public let steps: [ProvisioningStep]
    public init(_ steps: [ProvisioningStep]) { self.steps = steps }

    /// Runs every step that isn't done yet, in order, and stops at the first failure.
    /// Returns true if all the steps completed. Throws only `CancellationError`.
    public func run(progress: @escaping @Sendable (Int, StepStatus) -> Void, log: @escaping LogSink) async throws -> Bool {
        for (i, step) in steps.enumerated() {
            try Task.checkCancellation()
            progress(i, .checking)
            if await step.isDone() {
                progress(i, .alreadyDone)
                continue
            }
            try Task.checkCancellation()
            progress(i, .running)
            log("== \(step.title)")
            do {
                try await step.run(log: log)
                progress(i, .done)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                if Task.isCancelled { throw CancellationError() }
                let message = error.localizedDescription
                log("!! \(step.title) failed: \(message)")
                progress(i, .failed(message))
                return false
            }
        }
        return true
    }
}

/// Assembles the setup plan from the wizard's answers. The same steps on every platform; the
/// platform supplies packages, services and files.
public enum ProvisioningPlan {
    /// - Parameters:
    ///   - dataDir: the node data directory chosen on the Node screen; nil keeps the current one.
    ///   - keys: baking keys to set up, or nil to not bake (a baker set up earlier is turned off).
    ///   - dal: DAL node settings; nil or disabled turns off one set up earlier.
    ///   - funding: used only on a test network when setup creates a master key.
    public static func make(platform: OctezPlatform, node settings: NodeSettings, dataDir: String? = nil,
                            keys: BakingKeyChoice? = nil, dal: DalSettings? = nil, baker: BakerSettings? = nil,
                            funding: FundingSettings? = nil) throws -> Provisioner {
        if let keys, !BakingKeyChoice.isAllowed(keys.setup, on: settings.network) {
            throw OctezDeskError("OctezDesk doesn't create a master key on \(settings.network.displayName).")
        }
        let node = NodeService(platform)
        let keyService = BakingKeyService(platform)
        var steps: [ProvisioningStep] = [InstallPackagesStep(platform, [.node, .client], title: "Install the Octez node and client")]
        if let dataDir { steps.append(SetDataDirStep(platform, service: .node, dataDir: dataDir)) }
        steps.append(ConfigureNodeStep(platform, node: node, settings: settings))
        if settings.importSnapshot { steps.append(ImportSnapshotStep(platform, node: node, settings: settings)) }
        steps.append(StartServiceStep(platform, .node))

        // Keys come after the node is up: octez-client is slow without one. The DAL node's attester
        // and the baker's keys come from them, so they follow.
        if let keys { steps.append(GenerateBakingKeysStep(keyService, choice: keys, node: node)) }
        if let dal, dal.enabled {
            steps.append(InstallPackagesStep(platform, [.dalNode], title: "Install the DAL node"))
            steps.append(ConfigureDalNodeStep(platform, settings: dal, keys: keyService))
            steps.append(StartServiceStep(platform, .dalNode))
        } else {
            steps.append(DisableServiceStep(platform, .dalNode))
        }
        if keys != nil {
            steps.append(InstallPackagesStep(platform, [.baker], title: "Install the baker"))
            steps.append(ConfigureBakerStep(platform, keys: keyService, dal: dal, settings: baker ?? BakerSettings()))
            steps.append(StartServiceStep(platform, .baker))
        } else {
            steps.append(DisableServiceStep(platform, .baker))
        }

        // Last, so everything else is set up even if the faucet is down, and the node has had the
        // longest to catch up, which injecting operations needs.
        if let funding, funding.fund, FundingSettings.applies(to: settings.network, keys: keys) {
            let account = MasterAccount(platform)
            steps.append(FundFromFaucetStep(account, faucet: try Faucet.for(settings.network), settings: funding, network: settings.network))
            if funding.stake {
                steps.append(RegisterDelegateStep(account, withCompanion: keys?.setup == .masterAndCompanion))
                steps.append(StakeStep(account, settings: funding))
            }
        }
        return Provisioner(steps)
    }

    /// From the dashboard's "Set up baker keys…" when no keys are recorded: only the keys.
    public static func keysOnly(platform: OctezPlatform, keys: BakingKeyChoice) -> Provisioner {
        Provisioner([GenerateBakingKeysStep(BakingKeyService(platform), choice: keys, node: NodeService(platform))])
    }
}
