import Foundation
import Testing
import OctezDeskCore
@testable import OctezDeskHomebrew

@Suite struct HomebrewPlatformTests {
    func tempHome() -> String {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("octezdesk-test-\(UUID().uuidString)").path
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        return dir
    }

    @Test func paths_follow_the_tap() {
        let p = HomebrewPlatform(brew: "/opt/homebrew/bin/brew", home: "/Users/x")
        #expect(p.prefix == "/opt/homebrew")
        #expect(p.serviceConfigPath(.baker) == "/opt/homebrew/etc/octez/octez-baker.conf")
        #expect(p.octezToolPath("octez-node") == "/opt/homebrew/bin/octez-node")
        #expect(p.plistPath(.node) == "/Users/x/Library/LaunchAgents/sh.brew.octez-node.plist")
        #expect(p.statePath(".octezdesk-node.json") == "/Users/x/Library/Application Support/OctezDesk/.octezdesk-node.json")
    }

    @Test func formula_names_are_qualified_with_their_tap() throws {
        let home = tempHome()
        let p = HomebrewPlatform(brew: home + "/bin/brew", home: home)
        #expect(p.qualifiedName("octez-node") == "drchrispinnock/octez/octez-node") // not installed: our tap
        let opt = home + "/opt/octez-node"
        try FileManager.default.createDirectory(atPath: opt, withIntermediateDirectories: true)
        try Data(#"{"source":{"tap":"local/octez"}}"#.utf8).write(to: URL(fileURLWithPath: opt + "/INSTALL_RECEIPT.json"))
        #expect(p.qualifiedName("octez-node") == "local/octez/octez-node")
    }

    @Test func service_label_follows_the_formulas_plist() throws {
        let home = tempHome()
        let p = HomebrewPlatform(brew: home + "/bin/brew", home: home)
        #expect(p.label(.dalNode) == "sh.brew.octez-dal-node")
        let opt = home + "/opt/octez-dal-node"
        try FileManager.default.createDirectory(atPath: opt, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: opt + "/homebrew.mxcl.octez-dal-node.plist", contents: Data())
        #expect(p.label(.dalNode) == "homebrew.mxcl.octez-dal-node")
    }

    @Test func a_job_runs_detached_and_reports_its_result() async throws {
        let p = HomebrewPlatform(brew: "/opt/homebrew/bin/brew", home: tempHome())
        try await p.startJob("ok", script: "echo \"hello $1\"; sleep 1", arguments: ["world"])
        #expect(await p.jobState("ok") == .running)
        for _ in 0..<50 where await p.jobState("ok") == .running { try await Task.sleep(nanoseconds: 100_000_000) }
        #expect(await p.jobState("ok") == .succeeded)
        #expect(await p.readFile(p.jobLogPath("ok"))?.contains("hello world") == true)

        try await p.startJob("bad", script: "exit 3", arguments: [])
        for _ in 0..<50 where await p.jobState("bad") == .running { try await Task.sleep(nanoseconds: 100_000_000) }
        #expect(await p.jobState("bad") == .failed("exit 3"))
    }

    @Test func a_job_can_be_cancelled() async throws {
        let p = HomebrewPlatform(brew: "/opt/homebrew/bin/brew", home: tempHome())
        try await p.startJob("long", script: "sleep 30", arguments: [])
        #expect(await p.jobState("long") == .running)
        await p.cancelJob("long")
        #expect(await p.jobState("long") == JobState.none)
    }
}
