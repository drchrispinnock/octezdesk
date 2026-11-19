import Foundation

/// Receives log lines. Called from background threads.
public typealias LogSink = @Sendable (String) -> Void

public struct CommandResult: Sendable, Equatable {
    public var exitCode: Int32
    public var stdout: String
    public var stderr: String

    public init(exitCode: Int32, stdout: String = "", stderr: String = "") {
        self.exitCode = exitCode
        self.stdout = stdout
        self.stderr = stderr
    }

    public var success: Bool { exitCode == 0 }

    /// Throws a `CommandError` describing `what` unless the command succeeded.
    @discardableResult
    public func ensureSuccess(_ what: String) throws -> CommandResult {
        guard success else {
            // Some tools report errors on stdout.
            let err = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            let detail = err.isEmpty ? stdout.trimmingCharacters(in: .whitespacesAndNewlines) : err
            throw CommandError(message: "\(what) failed (exit \(exitCode)): \(lastLines(detail))", result: self)
        }
        return self
    }

    private func lastLines(_ s: String, count: Int = 15) -> String {
        let lines = s.split(separator: "\n", omittingEmptySubsequences: false)
        return lines.count <= count ? s : "…\n" + lines.suffix(count).joined(separator: "\n")
    }
}

public struct CommandError: Error, LocalizedError {
    public var message: String
    public var result: CommandResult?
    public init(message: String, result: CommandResult? = nil) {
        self.message = message
        self.result = result
    }
    public var errorDescription: String? { message }
}

/// A plain error with a message for the user.
public struct OctezDeskError: Error, LocalizedError, Equatable {
    public var message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// Runs a program directly (no shell), capturing its output and optionally streaming it line by line.
/// Output is joined with LF. Cancelling the calling task terminates the process.
public enum ProcessRunner {
    public static func run(
        _ executable: String,
        _ arguments: [String],
        environment: [String: String]? = nil,
        currentDirectory: String? = nil,
        stdin: String? = nil,
        onLine: LogSink? = nil
    ) async -> CommandResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        if let environment { process.environment = environment }
        if let currentDirectory { process.currentDirectoryURL = URL(fileURLWithPath: currentDirectory) }

        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err
        let input = Pipe()
        process.standardInput = input

        let collector = OutputCollector(onLine: onLine)

        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<CommandResult, Never>) in
                collector.onFinish = { code in
                    continuation.resume(returning: CommandResult(
                        exitCode: code, stdout: collector.text(.out), stderr: collector.text(.err)))
                }
                out.fileHandleForReading.readabilityHandler = { h in
                    let data = h.availableData
                    if data.isEmpty { h.readabilityHandler = nil; collector.close(.out) } else { collector.append(data, .out) }
                }
                err.fileHandleForReading.readabilityHandler = { h in
                    let data = h.availableData
                    if data.isEmpty { h.readabilityHandler = nil; collector.close(.err) } else { collector.append(data, .err) }
                }
                process.terminationHandler = { p in collector.exited(p.terminationStatus) }
                do {
                    try process.run()
                    collector.started(process)
                } catch {
                    out.fileHandleForReading.readabilityHandler = nil
                    err.fileHandleForReading.readabilityHandler = nil
                    continuation.resume(returning: CommandResult(
                        exitCode: 127, stderr: "Couldn't run \(executable): \(error.localizedDescription)"))
                    return
                }
                if let stdin { input.fileHandleForWriting.write(Data(stdin.utf8)) }
                try? input.fileHandleForWriting.close()
            }
        } onCancel: {
            collector.terminate()
        }
    }
}

/// Gathers stdout and stderr, splits them into lines for the callback, and reports when the
/// process has exited and both pipes are drained.
private final class OutputCollector: @unchecked Sendable {
    enum Stream { case out, err }

    private let lock = NSLock()
    private let onLine: LogSink?
    private var data: [Stream: Data] = [.out: Data(), .err: Data()]
    private var partial: [Stream: Data] = [.out: Data(), .err: Data()]
    private var open: Set<Stream> = [.out, .err]
    private var exitCode: Int32?
    private var process: Process?
    private var cancelled = false
    var onFinish: ((Int32) -> Void)?

    init(onLine: LogSink?) { self.onLine = onLine }

    func started(_ p: Process) {
        lock.lock()
        process = p
        let kill = cancelled
        lock.unlock()
        if kill { p.terminate() }
    }

    func terminate() {
        lock.lock()
        cancelled = true
        let p = process
        lock.unlock()
        if let p, p.isRunning { p.terminate() }
    }

    func append(_ chunk: Data, _ s: Stream) {
        var lines: [String] = []
        lock.lock()
        data[s]!.append(chunk)
        if onLine != nil {
            partial[s]!.append(chunk)
            while let nl = partial[s]!.firstIndex(of: 0x0A) {
                let lineData = partial[s]![partial[s]!.startIndex..<nl]
                lines.append(Self.decode(lineData))
                partial[s]!.removeSubrange(partial[s]!.startIndex...nl)
            }
        }
        lock.unlock()
        for line in lines { onLine?(line) }
    }

    func close(_ s: Stream) {
        var rest: String?
        lock.lock()
        open.remove(s)
        if onLine != nil, !partial[s]!.isEmpty { rest = Self.decode(partial[s]!); partial[s] = Data() }
        lock.unlock()
        if let rest { onLine?(rest) }
        finishIfDone()
    }

    func exited(_ code: Int32) {
        lock.lock()
        exitCode = code
        lock.unlock()
        finishIfDone()
    }

    func text(_ s: Stream) -> String {
        lock.lock()
        defer { lock.unlock() }
        return Self.decode(data[s]!).replacingOccurrences(of: "\r\n", with: "\n")
    }

    private func finishIfDone() {
        lock.lock()
        guard open.isEmpty, let code = exitCode, let finish = onFinish else { lock.unlock(); return }
        onFinish = nil
        lock.unlock()
        finish(code)
    }

    private static func decode(_ d: Data) -> String {
        var s = String(decoding: d, as: UTF8.self)
        if s.hasSuffix("\r") { s.removeLast() }
        return s
    }
}
