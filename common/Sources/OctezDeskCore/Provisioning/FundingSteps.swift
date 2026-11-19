import Foundation

/// The master key's on-chain state, through the local node.
public final class MasterAccount: @unchecked Sendable {
    public let client: OctezClient
    let node: NodeService

    public init(_ platform: OctezPlatform) {
        client = OctezClient(platform)
        node = NodeService(platform)
    }

    public func address() async -> String? { await client.showAddress(BakingKeyService.masterAlias)?.address }

    public func requireAddress() async throws -> String {
        guard let a = await address() else { throw OctezDeskError("The wallet has no master key '\(BakingKeyService.masterAlias)'.") }
        return a
    }

    public func status() async -> MasterAccountStatus? {
        guard let address = await address() else { return nil }
        let rpc = await node.rpc()
        guard let balance = try? await rpc.balance(address), let staked = try? await rpc.stakedBalance(address) else { return nil }
        return MasterAccountStatus(address: address, balance: balance, staked: staked, delegate: await rpc.delegate(address),
                                   unstakedFrozen: (try? await rpc.unstakedFrozenBalance(address)) ?? 0,
                                   unstakedFinalizable: (try? await rpc.unstakedFinalizableBalance(address)) ?? 0)
    }

    /// Spendable plus staked, in mutez; nil if the node can't be asked.
    public func totalBalance(_ address: String) async -> Int64? {
        let rpc = await node.rpc()
        guard let b = try? await rpc.balance(address), let s = try? await rpc.stakedBalance(address) else { return nil }
        return b + s
    }

    public func staked() async -> Int64? {
        guard let a = await address() else { return nil }
        return try? await node.rpc().stakedBalance(a)
    }

    public func register(withCompanion: Bool, log: LogSink? = nil) async throws {
        try await client.waitForBootstrapped(log: log)
        try await client.registerDelegate(BakingKeyService.masterAlias, companion: withCompanion ? BakingKeyService.companionAlias : nil, log: log)
    }

    public func unstake(_ mutez: Int64?, log: LogSink? = nil) async throws {
        try await client.waitForBootstrapped(log: log)
        try await client.unstake(mutez, for: BakingKeyService.masterAlias, log: log)
    }

    public func finalizeUnstake(log: LogSink? = nil) async throws {
        try await client.waitForBootstrapped(log: log)
        try await client.finalizeUnstake(for: BakingKeyService.masterAlias, log: log)
    }
}

public struct MasterAccountStatus: Sendable, Equatable {
    public var address: String
    public var balance: Int64
    public var staked: Int64
    public var delegate: DelegateInfo?
    public var unstakedFrozen: Int64
    public var unstakedFinalizable: Int64
    public var isDelegate: Bool { delegate != nil }
}

/// Gets test tez for the master key from the faucet, then waits until the local node sees them.
/// Done when the key holds the amount (less the fee allowance), counting its stake.
public final class FundFromFaucetStep: ProvisioningStep, @unchecked Sendable {
    let account: MasterAccount
    let faucet: Faucet
    let settings: FundingSettings
    let network: TezosNetwork

    public init(_ account: MasterAccount, faucet: Faucet, settings: FundingSettings, network: TezosNetwork) {
        self.account = account
        self.faucet = faucet
        self.settings = settings
        self.network = network
    }

    public var title: String { "Get \(settings.amount.formatted()) ꜩ from the \(network.displayName) faucet" }

    var target: Int64 { Int64(settings.amount - FundingSettings.feeAllowance) * TezAmount.mutezPerTez }

    public func isDone() async -> Bool {
        guard let a = await account.address(), let b = await account.totalBalance(a) else { return false }
        return b >= target
    }

    public func run(log: @escaping LogSink) async throws {
        let address = try await account.requireAddress()
        log("Waiting for the node to be bootstrapped, to read the balance…")
        try await account.client.waitForBootstrapped(log: log)
        let have = await account.totalBalance(address) ?? 0
        let wanted = Int64(settings.amount) * TezAmount.mutezPerTez
        let missing = max(1, Int((wanted - have + TezAmount.mutezPerTez - 1) / TezAmount.mutezPerTez))
        log("Requesting \(missing.formatted()) ꜩ for \(address) from \(faucet.baseURL). This can take a few minutes.")
        let hash = try await faucet.request(address: address, amount: missing, log: log)
        log("The faucet sent the tez (operation \(hash)). Waiting for it to reach the node…")
        let deadline = Date().addingTimeInterval(600)
        while (await account.totalBalance(address) ?? 0) < target {
            if Date() >= deadline {
                throw OctezDeskError("The faucet's transfer \(hash) hasn't reached the node after 10 minutes. Retry to check again.")
            }
            try await Task.sleep(nanoseconds: 5_000_000_000)
        }
        log("The master key now holds \(TezAmount.format(await account.totalBalance(address) ?? 0)) ꜩ.")
    }
}

public final class RegisterDelegateStep: ProvisioningStep, @unchecked Sendable {
    let account: MasterAccount
    let withCompanion: Bool

    public init(_ account: MasterAccount, withCompanion: Bool) {
        self.account = account
        self.withCompanion = withCompanion
    }

    public var title: String {
        "Register \"\(BakingKeyService.masterAlias)\" as a baker" + (withCompanion ? ", with its companion key" : "")
    }

    public func isDone() async -> Bool {
        guard let a = await account.address() else { return false }
        return await account.node.rpc().isDelegate(a)
    }

    public func run(log: @escaping LogSink) async throws { try await account.register(withCompanion: withCompanion, log: log) }
}

/// Stakes `stakeAmount` from the master key, topping up what's already staked.
public final class StakeStep: ProvisioningStep, @unchecked Sendable {
    let account: MasterAccount
    let settings: FundingSettings

    public init(_ account: MasterAccount, settings: FundingSettings) {
        self.account = account
        self.settings = settings
    }

    public var title: String { "Stake \(settings.stakeAmount.formatted()) ꜩ" }

    var target: Int64 { Int64(settings.stakeAmount) * TezAmount.mutezPerTez }

    public func isDone() async -> Bool { (await account.staked() ?? 0) >= target }

    public func run(log: @escaping LogSink) async throws {
        try await account.client.waitForBootstrapped(log: log)
        let staked = await account.staked() ?? 0
        let tez = (target - staked + TezAmount.mutezPerTez - 1) / TezAmount.mutezPerTez
        try await account.client.stake(tez, for: BakingKeyService.masterAlias, log: log)
        log("Staked. Baking rights start a few cycles from now.")
    }
}
