import Foundation

public enum HealthLevel: Sendable, Equatable {
    /// Not working.
    case red
    /// Running but degraded (syncing, stuck, no peers).
    case yellow
    /// Running and synced.
    case green
}

/// Raw facts gathered about the node. Nil means "could not be determined".
public struct NodeObservation: Sendable, Equatable {
    public var serviceActive = false
    public var rpcReachable = false
    public var bootstrapped: Bool?
    /// "synced", "unsynced" or "stuck", from `/chains/main/is_bootstrapped`.
    public var syncState: String?
    public var peerCount: Int?
    public var headLevel: Int?
    public var headTimestamp: Date?
    public var version: String?

    public init(serviceActive: Bool = false, rpcReachable: Bool = false, bootstrapped: Bool? = nil, syncState: String? = nil,
                peerCount: Int? = nil, headLevel: Int? = nil, headTimestamp: Date? = nil, version: String? = nil) {
        self.serviceActive = serviceActive
        self.rpcReachable = rpcReachable
        self.bootstrapped = bootstrapped
        self.syncState = syncState
        self.peerCount = peerCount
        self.headLevel = headLevel
        self.headTimestamp = headTimestamp
        self.version = version
    }
}

public struct NodeHealth: Sendable, Equatable {
    public var level: HealthLevel
    public var summary: String
    public var observation: NodeObservation
}

/// The health rules, the same on every platform. Ported from the Windows HealthEvaluator; change them here and in its tests.
public enum HealthEvaluator {
    public static func evaluate(_ o: NodeObservation, now: Date = Date()) -> NodeHealth {
        func h(_ l: HealthLevel, _ s: String) -> NodeHealth { NodeHealth(level: l, summary: s, observation: o) }
        if !o.serviceActive { return h(.red, "Not running") }
        if !o.rpcReachable { return h(.red, "Not responding (it may still be starting)") }
        if o.syncState == "stuck" { return h(.yellow, "Stuck — not receiving new blocks") }
        if o.bootstrapped != true || o.syncState != "synced" { return h(.yellow, catchingUp(o.headTimestamp, now)) }
        if o.peerCount == 0 { return h(.yellow, "No peers") }
        return h(.green, "Synced")
    }

    static func catchingUp(_ head: Date?, _ now: Date) -> String {
        if let head, now.timeIntervalSince(head) > 60 { return "Catching up — \(describe(now.timeIntervalSince(head))) behind" }
        return "Catching up"
    }

    public static func describe(_ age: TimeInterval) -> String {
        switch age {
        case ..<60: "\(Int(age)) s"
        case ..<3600: "\(Int(age / 60)) min"
        case ..<(2 * 86400): "\(Int(age / 3600)) h"
        default: "\(Int(age / 86400)) days"
        }
    }
}

/// A service's state for a dashboard row (DAL node, baker). `level` nil: not set up (grey dot).
public struct ServiceHealth: Sendable, Equatable {
    public static let notSetUpHint = "To add one, use the menu: Set up…"

    public var level: HealthLevel?
    public var summary: String
    public var details: String

    public static func evaluate(installed: Bool, enabled: Bool, active: Bool, details: String) -> ServiceHealth {
        if !installed || (!enabled && !active) { return ServiceHealth(level: nil, summary: "Not set up", details: notSetUpHint) }
        return active
            ? ServiceHealth(level: .green, summary: "Running", details: details)
            : ServiceHealth(level: .red, summary: "Not running", details: details)
    }

    /// "Keys consensus, companion · LB vote pass · with DAL", from the baker's settings file.
    public static func describeBaker(_ config: String?) -> String {
        guard let config else { return "" }
        let d = ShellConfigFile.parse(config)
        var parts: [String] = []
        if let keys = d["KEYS"], !keys.isEmpty { parts.append("Keys " + keys.split(separator: " ").joined(separator: ", ")) }
        if let vote = d["LQ_VOTE"], !vote.isEmpty { parts.append("LB vote " + vote) }
        parts.append(d["DAL_OPTIONS"]?.contains("--dal-node") == true ? "with DAL" : "without DAL")
        return parts.joined(separator: " · ")
    }

    public static func describeDal(_ r: DalSettingsRecord?) -> String {
        guard let r else { return "" }
        return (r.attester.map { "Attester \($0)" } ?? "No attester profile") + " · RPC \(r.rpcPort) · P2P \(r.p2pPort)"
    }
}
