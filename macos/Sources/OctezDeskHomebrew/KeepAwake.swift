import Foundation
import OctezDeskCore

/// "Keep this Mac awake": a launchd agent running `caffeinate -i -s`, so the Mac doesn't idle-sleep
/// (and, on power, doesn't sleep at all) and the baker doesn't miss its slots. Opt-in from the menu.
/// It starts again at login, until it's turned off.
public enum KeepAwake {
    public static let label = "org.octezdesk.keep-awake"

    static var plistPath: String {
        (NSHomeDirectory() as NSString).appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    public static var isOn: Bool { FileManager.default.fileExists(atPath: plistPath) }

    public static func set(_ on: Bool) async throws {
        let domain = "gui/\(getuid())"
        if on {
            let plist: [String: Any] = [
                "Label": label,
                "ProgramArguments": ["/usr/bin/caffeinate", "-i", "-s"],
                "RunAtLoad": true,
                "KeepAlive": true,
            ]
            let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
            try FileManager.default.createDirectory(atPath: (plistPath as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            try data.write(to: URL(fileURLWithPath: plistPath), options: .atomic)
            _ = await ProcessRunner.run("/bin/launchctl", ["bootout", "\(domain)/\(label)"])
            try await ProcessRunner.run("/bin/launchctl", ["bootstrap", domain, plistPath]).ensureSuccess("Keeping the Mac awake")
        } else {
            _ = await ProcessRunner.run("/bin/launchctl", ["bootout", "\(domain)/\(label)"])
            try? FileManager.default.removeItem(atPath: plistPath)
        }
    }
}

/// Homebrew itself is needed first. Installing it asks for an administrator password in a
/// terminal, so OctezDesk opens Terminal with the official command rather than running it.
public enum HomebrewInstaller {
    public static let command = #"/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)""#

    /// Writes a `.command` file and opens it, which runs it in Terminal.
    public static func openInTerminal() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("OctezDesk", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appendingPathComponent("Install Homebrew.command")
        let script = """
            #!/bin/bash
            echo "Installing Homebrew for OctezDesk. It will ask for your password."
            echo
            \(command)
            echo
            echo "When it has finished, go back to OctezDesk and click Check again."
            """
        try Data(script.utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        return file
    }
}
