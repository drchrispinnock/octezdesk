import Foundation
import OctezDeskCore

/// OctezDesk on macOS: Octez from the `drchrispinnock/octez` Homebrew tap, run as launchd agents
/// by `brew services` as the logged-in user. Settings files are `$(brew --prefix)/etc/octez/*.conf`,
/// data is in `~/.tezos-node` and friends, and nothing needs administrator rights.
public final class HomebrewPlatform: OctezPlatform, @unchecked Sendable {
    public static let tap = "drchrispinnock/octez"

    /// The `brew` executable.
    public let brew: String
    /// `brew --prefix`: /opt/homebrew on Apple silicon, /usr/local on Intel.
    public let prefix: String
    public let serviceHome: String
    public let stateDirectory: String

    public var defaultNodeDataDir: String { (serviceHome as NSString).appendingPathComponent(".tezos-node") }
    public var defaultDalDataDir: String { (serviceHome as NSString).appendingPathComponent(".tezos-dal-node") }
    public var clientDir: String { (serviceHome as NSString).appendingPathComponent(".tezos-client") }

    /// Nil if Homebrew isn't installed.
    public convenience init?(home: String = NSHomeDirectory()) {
        guard let brew = Self.findBrew() else { return nil }
        self.init(brew: brew, home: home)
    }

    public init(brew: String, home: String) {
        self.brew = brew
        prefix = ((brew as NSString).deletingLastPathComponent as NSString).deletingLastPathComponent
        serviceHome = home
        stateDirectory = (home as NSString).appendingPathComponent("Library/Application Support/OctezDesk")
    }

    public static func findBrew() -> String? {
        var candidates = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
        if let p = ProcessInfo.processInfo.environment["HOMEBREW_PREFIX"] { candidates.insert(p + "/bin/brew", at: 0) }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// GUI apps get a minimal PATH; brew and Octez need Homebrew's.
    var environment: [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = "\(prefix)/bin:\(prefix)/sbin:/usr/bin:/bin:/usr/sbin:/sbin"
        env["HOME"] = serviceHome
        env["NONINTERACTIVE"] = "1"
        env["HOMEBREW_NO_ENV_HINTS"] = "1"
        env["HOMEBREW_NO_COLOR"] = "1"
        env["TEZOS_CLIENT_UNSAFE_DISABLE_DISCLAIMER"] = "Y"
        if env["LANG"] == nil { env["LANG"] = "en_US.UTF-8" }
        return env
    }

    func brew(_ args: [String], log: LogSink? = nil) async -> CommandResult {
        await ProcessRunner.run(brew, args, environment: environment, currentDirectory: serviceHome, onLine: log)
    }

    // MARK: Packages

    public func isInstalled(_ package: OctezPackage) async -> Bool {
        FileManager.default.fileExists(atPath: "\(prefix)/opt/\(package.rawValue)/bin")
    }

    public func install(_ packages: [OctezPackage], log: @escaping LogSink) async throws {
        guard !packages.isEmpty else { return }
        log("$ brew tap \(Self.tap)")
        try await brew(["tap", Self.tap], log: log).ensureSuccess("brew tap \(Self.tap)")
        // Homebrew 7 needs a third-party tap to be trusted before it installs its dependencies.
        let trust = await brew(["trust", Self.tap], log: log)
        if !trust.success { log("(brew trust isn't available or failed; carrying on.)") }
        let names = packages.map { "\(Self.tap)/\($0.rawValue)" }
        log("$ brew install \(names.joined(separator: " "))")
        try await brew(["install"] + names, log: log).ensureSuccess("brew install")
    }

    public func upgrade(log: @escaping LogSink) async throws {
        log("$ brew update")
        try await brew(["update"], log: log).ensureSuccess("brew update")
        var names: [String] = []
        for p in OctezPackage.allCases where await isInstalled(p) { names.append(qualifiedName(p.rawValue)) }
        log("$ brew upgrade \(names.joined(separator: " "))")
        try await brew(["upgrade"] + names, log: log).ensureSuccess("brew upgrade")
    }

    /// `tap/formula`, from the tap the formula was installed from (its install receipt), else our tap.
    /// brew refuses a bare name when several taps have the formula, e.g. a local dev tap as well.
    public func qualifiedName(_ formula: String) -> String {
        "\(installedTap(formula) ?? Self.tap)/\(formula)"
    }

    func installedTap(_ formula: String) -> String? {
        let receipt = "\(prefix)/opt/\(formula)/INSTALL_RECEIPT.json"
        guard let data = FileManager.default.contents(atPath: receipt),
              let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let tap = (json["source"] as? [String: Any])?["tap"] as? String, tap.contains("/") else { return nil }
        return tap
    }

    public func installedVersion(_ package: OctezPackage) async -> String? {
        // opt/<formula> links to Cellar/<formula>/<version>.
        let opt = "\(prefix)/opt/\(package.rawValue)"
        guard let dest = try? FileManager.default.destinationOfSymbolicLink(atPath: opt) else { return nil }
        return (dest as NSString).lastPathComponent
    }

    // MARK: Services (launchd agents written by brew services)

    /// The launchd label `brew services` uses: `sh.brew.<formula>` from Homebrew 7, `homebrew.mxcl.<formula>`
    /// before. Taken from the plist the formula installs, else from an agent already registered.
    func label(_ s: OctezService) -> String {
        let names = ["sh.brew.\(s.rawValue)", "homebrew.mxcl.\(s.rawValue)"]
        let fm = FileManager.default
        if let n = names.first(where: { fm.fileExists(atPath: "\(prefix)/opt/\(s.rawValue)/\($0).plist") }) { return n }
        let agents = (serviceHome as NSString).appendingPathComponent("Library/LaunchAgents")
        return names.first { fm.fileExists(atPath: "\(agents)/\($0).plist") } ?? names[0]
    }

    func plistPath(_ s: OctezService) -> String {
        (serviceHome as NSString).appendingPathComponent("Library/LaunchAgents/\(label(s)).plist")
    }

    var domain: String { "gui/\(getuid())" }

    func launchctl(_ args: [String]) async -> CommandResult { await ProcessRunner.run("/bin/launchctl", args) }

    public func serviceState(_ s: OctezService) async -> ServiceState {
        let enabled = FileManager.default.fileExists(atPath: plistPath(s))
        let r = await launchctl(["list", label(s)])
        return ServiceState(enabled: enabled, active: r.success && r.stdout.contains("\"PID\" ="))
    }

    func isLoaded(_ s: OctezService) async -> Bool { await launchctl(["list", label(s)]).success }

    public func enableAndStart(_ s: OctezService) async throws {
        let loaded = await isLoaded(s)
        if loaded, !FileManager.default.fileExists(atPath: plistPath(s)) {
            _ = await launchctl(["bootout", "\(domain)/\(label(s))"])
        }
        try await brew(["services", "start", qualifiedName(s.rawValue)]).ensureSuccess("Starting the \(s.displayName)")
        if !(await serviceState(s).active) { try await start(s) }
    }

    public func start(_ s: OctezService) async throws {
        guard FileManager.default.fileExists(atPath: plistPath(s)) else { return try await enableAndStart(s) }
        if await isLoaded(s) {
            try await launchctl(["kickstart", "\(domain)/\(label(s))"]).ensureSuccess("Starting the \(s.displayName)")
        } else {
            try await bootstrap(s)
        }
    }

    /// Unloads the agent; its plist stays, so it starts again at the next login.
    public func stop(_ s: OctezService) async throws {
        guard await isLoaded(s) else { return }
        // bootout sends SIGTERM and waits for the service's exit timeout (5 minutes for the node).
        try await launchctl(["bootout", "\(domain)/\(label(s))"]).ensureSuccess(ServiceControl.describe(s, .stop))
    }

    public func restart(_ s: OctezService) async throws {
        try await stop(s)
        try await start(s)
    }

    public func disable(_ s: OctezService) async throws {
        let loaded = await isLoaded(s)
        guard FileManager.default.fileExists(atPath: plistPath(s)) || loaded else { return }
        try await brew(["services", "stop", qualifiedName(s.rawValue)]).ensureSuccess("Turning off the \(s.displayName)")
    }

    /// Loads the agent. Right after a bootout launchd can refuse for a moment, so this retries.
    private func bootstrap(_ s: OctezService) async throws {
        var last = CommandResult(exitCode: 1)
        for attempt in 0..<5 {
            if attempt > 0 { try await Task.sleep(nanoseconds: 2_000_000_000) }
            last = await launchctl(["bootstrap", domain, plistPath(s)])
            if last.success { return }
            if await isLoaded(s) { return }
        }
        try last.ensureSuccess("Starting the \(s.displayName)")
    }

    public func serviceConfigPath(_ s: OctezService) -> String { "\(prefix)/etc/octez/\(s.rawValue).conf" }

    /// The newest Octez daily log (info level). The service log in `var/log/octez` only gets warnings.
    public func logFile(_ s: OctezService) async -> String? {
        let dir: String
        switch s {
        case .node: dir = (await NodeService(self).dataDir() as NSString).appendingPathComponent("daily_logs")
        case .dalNode: dir = (await DalNodeService(self).dataDir() as NSString).appendingPathComponent("daily_logs")
        case .baker: dir = (clientDir as NSString).appendingPathComponent("logs/octez-baker")
        }
        let logs = ((try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []).filter { $0.hasPrefix("daily") && $0.hasSuffix(".log") }
        if let newest = logs.max() { return (dir as NSString).appendingPathComponent(newest) }
        let serviceLog = "\(prefix)/var/log/octez/\(s.rawValue).log"
        return FileManager.default.fileExists(atPath: serviceLog) ? serviceLog : nil
    }

    // MARK: Files

    public func readFile(_ path: String) async -> String? {
        guard let d = FileManager.default.contents(atPath: path) else { return nil }
        return String(decoding: d, as: UTF8.self)
    }

    public func writeFile(_ path: String, _ content: String) async throws {
        let dir = (path as NSString).deletingLastPathComponent
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        try Data(content.utf8).write(to: URL(fileURLWithPath: path), options: .atomic)
    }

    public func fileExists(_ path: String) async -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDir) && !isDir.boolValue
    }

    public func directoryExists(_ path: String) async -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDir) && isDir.boolValue
    }

    public func removeFile(_ path: String) async throws {
        if FileManager.default.fileExists(atPath: path) { try FileManager.default.removeItem(atPath: path) }
    }

    // MARK: Running Octez

    public func octezToolPath(_ tool: String) -> String { "\(prefix)/bin/\(tool)" }

    public func runOctez(_ tool: String, _ arguments: [String], stdin: String?, onLine: LogSink?) async -> CommandResult {
        await ProcessRunner.run(octezToolPath(tool), arguments, environment: environment, currentDirectory: serviceHome,
                                stdin: stdin ?? "", onLine: onLine)
    }

    // MARK: Jobs
    //
    // A job is a detached `sh` that outlives OctezDesk: jobs/<name>.pid holds its PID while it runs,
    // jobs/<name>.status its exit code once it's finished, and jobs/<name>.log its output.

    var jobsDir: String { (stateDirectory as NSString).appendingPathComponent("jobs") }
    func jobFile(_ name: String, _ ext: String) -> String { (jobsDir as NSString).appendingPathComponent("\(name).\(ext)") }
    public func jobLogPath(_ name: String) -> String { jobFile(name, "log") }

    static let jobWrapper = """
        #!/bin/sh
        # Runs an OctezDesk job: $1 pid file, $2 status file, $3 script, then the script's arguments.
        pidfile="$1"; statusfile="$2"; script="$3"; shift 3
        echo $$ > "$pidfile"
        rm -f "$statusfile"
        /bin/sh "$script" "$@" &
        child=$!
        trap 'pkill -TERM -P $child 2>/dev/null; kill -TERM $child 2>/dev/null; echo cancelled > "$statusfile"; rm -f "$pidfile"; exit 143' TERM INT HUP
        wait $child
        echo $? > "$statusfile"
        rm -f "$pidfile"
        """

    public func startJob(_ name: String, script: String, arguments: [String]) async throws {
        try FileManager.default.createDirectory(atPath: jobsDir, withIntermediateDirectories: true)
        let wrapper = jobFile("job-wrapper", "sh"), scriptFile = jobFile(name, "sh")
        try Data(Self.jobWrapper.utf8).write(to: URL(fileURLWithPath: wrapper))
        try Data(script.utf8).write(to: URL(fileURLWithPath: scriptFile))
        try? FileManager.default.removeItem(atPath: jobFile(name, "status"))
        FileManager.default.createFile(atPath: jobLogPath(name), contents: nil)
        // The outer sh starts the wrapper in the background with nohup and exits at once, so the
        // job is adopted by launchd and keeps going when OctezDesk quits.
        let launcher = #"nohup /bin/sh "$0" "$@" >>"$LOGFILE" 2>&1 </dev/null &"#
        var env = environment
        env["LOGFILE"] = jobLogPath(name)
        try await ProcessRunner.run("/bin/sh", ["-c", launcher, wrapper, jobFile(name, "pid"), jobFile(name, "status"), scriptFile] + arguments,
                                    environment: env, currentDirectory: serviceHome).ensureSuccess("Starting \(name)")
        // Wait for the wrapper to write its PID, so jobState says running straight away.
        for _ in 0..<50 where !FileManager.default.fileExists(atPath: jobFile(name, "pid")) && !FileManager.default.fileExists(atPath: jobFile(name, "status")) {
            try await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    public func jobState(_ name: String) async -> JobState {
        if let status = await readFile(jobFile(name, "status"))?.trimmingCharacters(in: .whitespacesAndNewlines) {
            return status == "0" ? .succeeded : .failed(status == "cancelled" ? "cancelled" : "exit \(status)")
        }
        guard let pidText = await readFile(jobFile(name, "pid")), let pid = pid_t(pidText.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            return .none
        }
        return kill(pid, 0) == 0 ? .running : .failed("interrupted")
    }

    public func cancelJob(_ name: String) async {
        if let pidText = await readFile(jobFile(name, "pid")), let pid = pid_t(pidText.trimmingCharacters(in: .whitespacesAndNewlines)) {
            kill(pid, SIGTERM)
            for _ in 0..<50 where kill(pid, 0) == 0 { try? await Task.sleep(nanoseconds: 100_000_000) }
        }
        try? FileManager.default.removeItem(atPath: jobFile(name, "pid"))
        try? FileManager.default.removeItem(atPath: jobFile(name, "status"))
    }

    // MARK: Logs

    public func follow(_ path: String, initialLines: Int, onLine: @escaping LogSink) async {
        // In-process rather than `tail -F`, which outlived the app as an orphan.
        await FileFollower.follow(path, initialLines: initialLines, onLine: onLine)
    }
}
