import Foundation
import Testing
@testable import OctezDeskCore

@Suite struct FileFollowerTests {
    final class Lines: @unchecked Sendable {
        private let lock = NSLock()
        private var items: [String] = []
        func add(_ s: String) { lock.lock(); items.append(s); lock.unlock() }
        var all: [String] { lock.lock(); defer { lock.unlock() }; return items }
    }

    func waitFor(_ lines: Lines, _ count: Int) async {
        for _ in 0..<100 where lines.all.count < count { try? await Task.sleep(nanoseconds: 20_000_000) }
    }

    func append(_ path: String, _ text: String) throws {
        let h = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
        try h.seekToEnd()
        try h.write(contentsOf: Data(text.utf8))
        try h.close()
    }

    @Test func shows_the_tail_then_new_lines() async throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("follow-\(UUID().uuidString).log").path
        try Data((1...500).map { "line \($0)\n" }.joined().appending("partial").utf8).write(to: URL(fileURLWithPath: path))
        let lines = Lines()
        let task = Task { await FileFollower.follow(path, initialLines: 3, pollInterval: 0.05, onLine: lines.add) }
        await waitFor(lines, 3)
        #expect(lines.all == ["line 498", "line 499", "line 500"])

        try append(path, " done\nline 501\n")
        await waitFor(lines, 5)
        #expect(lines.all.suffix(2) == ["partial done", "line 501"])

        // Truncated: starts again from the beginning.
        try Data("fresh\n".utf8).write(to: URL(fileURLWithPath: path))
        await waitFor(lines, 6)
        #expect(lines.all.last == "fresh")

        task.cancel()
        await task.value
    }

    @Test func waits_for_a_file_that_does_not_exist_yet() async throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("follow-\(UUID().uuidString).log").path
        let lines = Lines()
        let task = Task { await FileFollower.follow(path, initialLines: 10, pollInterval: 0.05, onLine: lines.add) }
        try await Task.sleep(nanoseconds: 150_000_000)
        try Data("hello\n".utf8).write(to: URL(fileURLWithPath: path))
        await waitFor(lines, 1)
        #expect(lines.all == ["hello"])
        task.cancel()
        await task.value
    }
}
