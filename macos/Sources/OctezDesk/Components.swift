import AppKit
import OctezDeskCore
import SwiftUI

/// The frame shared by the wizard screens: title, content, and Back / Next.
struct WizardPage<Content: View>: View {
    let step: String
    let title: String
    var subtitle: String?
    var back: (() -> Void)?
    var nextTitle = "Next"
    var problems: [String] = []
    let next: () -> Void
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text(step.uppercased()).font(.caption).foregroundStyle(.secondary)
                Text(title).font(.largeTitle).bold()
                if let subtitle { Text(subtitle).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            }
            .padding([.horizontal, .top], 28)
            .padding(.bottom, 12)

            Form { content }
                .formStyle(.grouped)

            Divider()
            HStack(alignment: .center) {
                if let back { Button("Back", action: back) }
                Spacer()
                if let first = problems.first {
                    Label(first, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).lineLimit(2).font(.callout)
                }
                Button(nextTitle, action: next)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!problems.isEmpty)
            }
            .padding(16)
        }
    }
}

struct StatusDot: View {
    let level: HealthLevel?
    var size: CGFloat = 12

    var color: Color {
        switch level {
        case .green: .green
        case .yellow: .yellow
        case .red: .red
        case nil: .gray.opacity(0.5)
        }
    }

    var body: some View {
        Circle().fill(color).frame(width: size, height: size)
            .overlay(Circle().stroke(.black.opacity(0.15), lineWidth: 0.5))
    }
}

/// A number field for ports.
struct PortField: View {
    let title: String
    @Binding var value: Int

    var body: some View {
        TextField(title, value: $value, format: .number.grouping(.never))
            .frame(maxWidth: 260)
    }
}

/// A value with a copy button: addresses, keys, commands.
struct CopyableField: View {
    let title: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(value, forType: .string)
                } label: { Label("Copy", systemImage: "doc.on.doc") }
                    .buttonStyle(.borderless).font(.caption)
            }
            Text(value).font(.system(.body, design: .monospaced)).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.vertical, 2)
    }
}

/// Collects log lines from background threads and publishes them in batches.
final class LogBuffer: ObservableObject, @unchecked Sendable {
    @Published private(set) var lines: [String] = []
    private let lock = NSLock()
    private var pending: [String] = []
    private var scheduled = false
    let limit: Int

    init(limit: Int = 3000) { self.limit = limit }

    nonisolated func append(_ line: String) {
        lock.lock()
        pending.append(line)
        let schedule = !scheduled
        scheduled = true
        lock.unlock()
        if schedule {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in self?.flush() }
        }
    }

    var sink: LogSink { { [weak self] line in self?.append(line) } }

    @MainActor func clear() {
        lock.lock(); pending = []; lock.unlock()
        lines = []
    }

    @MainActor private func flush() {
        lock.lock()
        let batch = pending
        pending = []
        scheduled = false
        lock.unlock()
        guard !batch.isEmpty else { return }
        var l = lines + batch
        if l.count > limit { l.removeFirst(l.count - limit) }
        lines = l
    }
}

struct LogView: View {
    @ObservedObject var log: LogBuffer
    var placeholder = "Nothing yet."

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView([.vertical]) {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if log.lines.isEmpty { Text(placeholder).foregroundStyle(.secondary) }
                    ForEach(Array(log.lines.enumerated()), id: \.offset) { i, line in
                        Text(line.isEmpty ? " " : line)
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(color(line))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .id(i)
                    }
                }
                .padding(8)
            }
            .background(Color(nsColor: .textBackgroundColor))
            .onChange(of: log.lines.count) { _, n in
                if n > 0 { proxy.scrollTo(n - 1, anchor: .bottom) }
            }
        }
    }

    func color(_ line: String) -> Color {
        if line.hasPrefix("!!") || line.contains(" ERROR ") || line.hasPrefix("<003>") { return .red }
        if line.hasPrefix("==") { return .accentColor }
        if line.contains(" WARN ") || line.hasPrefix("<004>") { return .orange }
        return .primary
    }
}
