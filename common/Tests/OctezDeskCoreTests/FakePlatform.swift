import Foundation
@testable import OctezDeskCore

/// An in-memory platform: files in a dictionary, services as flags, and Octez tool calls recorded
/// and answered by `octez`.
final class FakePlatform: OctezPlatform, @unchecked Sendable {
    private let lock = NSLock()
    var files: [String: String] = [:]
    var directories: Set<String> = []
    var installed: Set<OctezPackage> = []
    var states: [OctezService: ServiceState] = [:]
    var calls: [[String]] = []
    var jobs: [String: JobState] = [:]
    /// Answers an Octez tool call (tool first); default success with no output.
    var octez: (([String]) -> CommandResult)?

    let serviceHome = "/home/u"
    let stateDirectory = "/state"
    let defaultNodeDataDir = "/home/u/.tezos-node"
    let defaultDalDataDir = "/home/u/.tezos-dal-node"
    let clientDir = "/home/u/.tezos-client"

    private func locked<T>(_ f: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return f() }

    func isInstalled(_ p: OctezPackage) async -> Bool { locked { installed.contains(p) } }
    func install(_ ps: [OctezPackage], log: @escaping LogSink) async throws { locked { installed.formUnion(ps) } }
    func upgrade(log: @escaping LogSink) async throws {}
    func installedVersion(_ p: OctezPackage) async -> String? { "25.3" }

    func serviceState(_ s: OctezService) async -> ServiceState { locked { states[s] ?? ServiceState(enabled: false, active: false) } }
    func enableAndStart(_ s: OctezService) async throws { locked { states[s] = ServiceState(enabled: true, active: true) } }
    func start(_ s: OctezService) async throws { locked { states[s, default: .init(enabled: false, active: false)].active = true } }
    func stop(_ s: OctezService) async throws { locked { states[s, default: .init(enabled: false, active: false)].active = false } }
    func restart(_ s: OctezService) async throws { locked { calls.append(["restart", s.rawValue]) } }
    func disable(_ s: OctezService) async throws { locked { states[s] = ServiceState(enabled: false, active: false) } }

    func serviceConfigPath(_ s: OctezService) -> String { "/etc/octez/\(s.rawValue).conf" }
    func logFile(_ s: OctezService) async -> String? { nil }

    func readFile(_ path: String) async -> String? { locked { files[path] } }
    func writeFile(_ path: String, _ content: String) async throws { locked { files[path] = content } }
    func fileExists(_ path: String) async -> Bool { locked { files[path] != nil } }
    func directoryExists(_ path: String) async -> Bool { locked { directories.contains(path) } }
    func removeFile(_ path: String) async throws { _ = locked { files.removeValue(forKey: path) } }

    func runOctez(_ tool: String, _ arguments: [String], stdin: String?, onLine: LogSink?) async -> CommandResult {
        let argv = [tool] + arguments
        locked { calls.append(argv) }
        return octez?(argv) ?? CommandResult(exitCode: 0)
    }
    func octezToolPath(_ tool: String) -> String { "/bin/\(tool)" }

    func startJob(_ name: String, script: String, arguments: [String]) async throws { locked { jobs[name] = .succeeded } }
    func jobState(_ name: String) async -> JobState { locked { jobs[name] ?? JobState.none } }
    func jobLogPath(_ name: String) -> String { "/state/\(name).log" }
    func cancelJob(_ name: String) async { locked { jobs[name] = JobState.none } }
    func follow(_ path: String, initialLines: Int, onLine: @escaping LogSink) async {}
}
