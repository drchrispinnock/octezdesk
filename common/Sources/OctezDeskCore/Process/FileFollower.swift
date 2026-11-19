import Foundation

/// Follows a growing log file, like `tail -n N -F`, but in-process: there's no child process to
/// leave behind when the app quits or crashes (an orphaned `tail -F` keeps running for ever).
/// Copes with the file not existing yet, being truncated, or being replaced.
public enum FileFollower {
    /// Calls `onLine` with the last `initialLines` lines, then with each new line, until the task is cancelled.
    public static func follow(_ path: String, initialLines: Int, pollInterval: TimeInterval = 1, onLine: LogSink) async {
        var offset: UInt64 = 0
        var fileID: UInt64?
        var partial = Data()
        var first = true

        while !Task.isCancelled {
            if let (size, id) = stat(path) {
                if id != fileID || size < offset {
                    // A new or truncated file: start again from its beginning (or its tail, the first time).
                    if first, initialLines > 0 {
                        let (lines, end) = lastLines(path, count: initialLines, size: size)
                        for line in lines { onLine(line) }
                        offset = end
                    } else {
                        offset = first ? size : 0
                    }
                    fileID = id
                    partial = Data()
                }
                first = false
                if size > offset, let h = FileHandle(forReadingAtPath: path) {
                    // Read at most 4 MB per poll, so a burst doesn't stall.
                    let length = Int(min(size - offset, 4 << 20))
                    if (try? h.seek(toOffset: offset)) != nil, let data = try? h.read(upToCount: length) {
                        offset += UInt64(data.count)
                        partial.append(data)
                        while let nl = partial.firstIndex(of: 0x0A) {
                            onLine(decode(partial[partial.startIndex..<nl]))
                            partial.removeSubrange(partial.startIndex...nl)
                        }
                    }
                    try? h.close()
                }
            } else {
                first = false
            }
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
        }
    }

    static func stat(_ path: String) -> (size: UInt64, id: UInt64)? {
        guard let a = try? FileManager.default.attributesOfItem(atPath: path) else { return nil }
        let size = (a[.size] as? NSNumber)?.uint64Value ?? 0
        let id = (a[.systemFileNumber] as? NSNumber)?.uint64Value ?? 0
        return (size, id)
    }

    /// The last `count` complete lines before `size`, reading backwards in chunks (up to 8 MB), and
    /// the offset just after the last complete line, where following should carry on.
    static func lastLines(_ path: String, count: Int, size: UInt64) -> (lines: [String], end: UInt64) {
        guard let h = FileHandle(forReadingAtPath: path) else { return ([], 0) }
        defer { try? h.close() }
        let chunk: UInt64 = 64 << 10
        var start = size
        var data = Data()
        while start > 0 && data.filter({ $0 == 0x0A }).count <= count && size - start < 8 << 20 {
            let from = start > chunk ? start - chunk : 0
            guard (try? h.seek(toOffset: from)) != nil, let d = try? h.read(upToCount: Int(start - from)) else { break }
            data = d + data
            start = from
        }
        // Only complete lines: a final line without a newline is still being written.
        guard let lastNL = data.lastIndex(of: 0x0A) else { return ([], start) }
        let end = start + UInt64(data.distance(from: data.startIndex, to: lastNL) + 1)
        data = data[data.startIndex...lastNL]
        var lines = data.split(separator: 0x0A, omittingEmptySubsequences: false).dropLast().map { decode($0) }
        if start > 0, !lines.isEmpty { lines.removeFirst() } // probably cut off at the chunk boundary
        return (Array(lines.suffix(count)), end)
    }

    static func decode(_ d: Data) -> String {
        var s = String(decoding: d, as: UTF8.self)
        if s.hasSuffix("\r") { s.removeLast() }
        return s
    }
}
