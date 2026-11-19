import Foundation

/// The Octez services OctezDesk runs. The raw value is the package (formula) and service name,
/// the same on Homebrew and in the tzinit Debian packages.
public enum OctezService: String, CaseIterable, Sendable {
    case node = "octez-node"
    case dalNode = "octez-dal-node"
    case baker = "octez-baker"

    /// "node", "DAL node", "baker": for "Stop the baker?" and similar.
    public var displayName: String {
        switch self {
        case .node: "node"
        case .dalNode: "DAL node"
        case .baker: "baker"
        }
    }

    public var package: OctezPackage {
        switch self {
        case .node: .node
        case .dalNode: .dalNode
        case .baker: .baker
        }
    }
}

/// Octez packages (Debian) or formulae (Homebrew).
public enum OctezPackage: String, CaseIterable, Sendable {
    case node = "octez-node"
    case client = "octez-client"
    case dalNode = "octez-dal-node"
    case baker = "octez-baker"
}

public struct ServiceState: Sendable, Equatable {
    /// Starts by itself: at boot (systemd) or at login (launchd agent).
    public var enabled: Bool
    public var active: Bool
    public init(enabled: Bool, active: Bool) {
        self.enabled = enabled
        self.active = active
    }
}

/// A long-running job, such as a snapshot import, that keeps going when OctezDesk quits.
public enum JobState: Sendable, Equatable {
    case none
    case running
    case succeeded
    case failed(String)
}

/// Everything platform-specific that the shared code needs: packages, services, files and how to
/// run the Octez tools. On macOS it's Homebrew and launchd (`brew services`), running as the
/// logged-in user; on Linux it will be the tzinit Debian packages and systemd, with the `tezos`
/// user. Provisioning, health and key handling only talk to this protocol.
public protocol OctezPlatform: AnyObject, Sendable {
    // MARK: Packages

    func isInstalled(_ package: OctezPackage) async -> Bool
    func install(_ packages: [OctezPackage], log: @escaping LogSink) async throws
    /// Upgrades the Octez packages that are installed. Doesn't restart anything.
    func upgrade(log: @escaping LogSink) async throws
    func installedVersion(_ package: OctezPackage) async -> String?

    // MARK: Services

    func serviceState(_ service: OctezService) async -> ServiceState
    /// Starts the service and makes it start by itself from now on.
    func enableAndStart(_ service: OctezService) async throws
    /// Starts a service that was stopped, without changing whether it starts by itself.
    func start(_ service: OctezService) async throws
    /// Stops the service until the next boot or login: it still starts by itself then.
    func stop(_ service: OctezService) async throws
    func restart(_ service: OctezService) async throws
    /// Stops the service and stops it starting by itself. Its package and data are kept.
    func disable(_ service: OctezService) async throws

    /// The service's settings file: `KEY="value"` lines, sourced by a shell script
    /// (`/etc/default/octez-node`, or `$(brew --prefix)/etc/octez/octez-node.conf`).
    func serviceConfigPath(_ service: OctezService) -> String
    /// The most useful log to show for the service. It may change, e.g. daily logs.
    func logFile(_ service: OctezService) async -> String?

    // MARK: Files and paths, as the user the services run as

    func readFile(_ path: String) async -> String?
    func writeFile(_ path: String, _ content: String) async throws
    func fileExists(_ path: String) async -> Bool
    func directoryExists(_ path: String) async -> Bool
    func removeFile(_ path: String) async throws

    /// The service user's home, which `$HOME` in the settings files refers to.
    var serviceHome: String { get }
    /// Where OctezDesk keeps its records (`.octezdesk-node.json` and so on).
    var stateDirectory: String { get }
    var defaultNodeDataDir: String { get }
    var defaultDalDataDir: String { get }
    /// The service user's octez-client wallet, which the baker uses.
    var clientDir: String { get }

    // MARK: Running Octez

    /// Runs an Octez tool (`octez-node`, `octez-client`, `octez-dal-node`) as the service user.
    func runOctez(_ tool: String, _ arguments: [String], stdin: String?, onLine: LogSink?) async -> CommandResult
    /// The full path of an Octez tool, for scripts run as jobs.
    func octezToolPath(_ tool: String) -> String

    /// Starts `script` (POSIX sh, arguments `"$1"`…) as a job that outlives OctezDesk, as the service user.
    func startJob(_ name: String, script: String, arguments: [String]) async throws
    func jobState(_ name: String) async -> JobState
    /// The job's output, so the UI can follow it.
    func jobLogPath(_ name: String) -> String
    func cancelJob(_ name: String) async

    // MARK: Following logs

    /// Calls `onLine` with the last `initialLines` lines of the file and then new lines, until cancelled.
    func follow(_ path: String, initialLines: Int, onLine: @escaping LogSink) async
}

public extension OctezPlatform {
    func runOctez(_ tool: String, _ arguments: [String], onLine: LogSink? = nil) async -> CommandResult {
        await runOctez(tool, arguments, stdin: nil, onLine: onLine)
    }

    func statePath(_ file: String) -> String {
        (stateDirectory as NSString).appendingPathComponent(file)
    }

    /// Restarts the service if it's running.
    func tryRestart(_ service: OctezService) async throws {
        if await serviceState(service).active { try await restart(service) }
    }

    /// The settings file, parsed, with `$HOME` expanded; empty if it can't be read.
    func serviceConfig(_ service: OctezService) async -> [String: String] {
        guard let text = await readFile(serviceConfigPath(service)) else { return [:] }
        return ShellConfigFile.parse(text, home: serviceHome)
    }
}

/// The file names of OctezDesk's records, the same on every platform.
public enum OctezDeskFiles {
    public static let nodeSettings = ".octezdesk-node.json"
    public static let keys = ".octezdesk-keys.json"
    public static let dalSettings = ".octezdesk-dal.json"
}
