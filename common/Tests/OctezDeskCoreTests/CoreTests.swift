import Foundation
import Testing
@testable import OctezDeskCore

@Suite struct HealthEvaluatorTests {
    let now = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func not_running_is_red() {
        #expect(HealthEvaluator.evaluate(NodeObservation(serviceActive: false)).level == .red)
    }

    @Test func no_rpc_is_red() {
        let h = HealthEvaluator.evaluate(NodeObservation(serviceActive: true, rpcReachable: false))
        #expect(h.level == .red)
        #expect(h.summary.hasPrefix("Not responding"))
    }

    @Test func stuck_is_yellow() {
        let h = HealthEvaluator.evaluate(NodeObservation(serviceActive: true, rpcReachable: true, bootstrapped: true, syncState: "stuck"))
        #expect(h.level == .yellow)
        #expect(h.summary.hasPrefix("Stuck"))
    }

    @Test func catching_up_says_how_far_behind() {
        let o = NodeObservation(serviceActive: true, rpcReachable: true, bootstrapped: false, syncState: "unsynced",
                                headTimestamp: now.addingTimeInterval(-3 * 3600))
        let h = HealthEvaluator.evaluate(o, now: now)
        #expect(h.level == .yellow)
        #expect(h.summary == "Catching up — 3 h behind")
    }

    @Test func no_peers_is_yellow() {
        let o = NodeObservation(serviceActive: true, rpcReachable: true, bootstrapped: true, syncState: "synced", peerCount: 0)
        #expect(HealthEvaluator.evaluate(o).level == .yellow)
    }

    @Test func synced_is_green() {
        let o = NodeObservation(serviceActive: true, rpcReachable: true, bootstrapped: true, syncState: "synced", peerCount: 12)
        #expect(HealthEvaluator.evaluate(o) .level == .green)
    }

    @Test func service_rows() {
        #expect(ServiceHealth.evaluate(installed: false, enabled: true, active: true, details: "x").level == nil)
        #expect(ServiceHealth.evaluate(installed: true, enabled: false, active: false, details: "x").level == nil)
        #expect(ServiceHealth.evaluate(installed: true, enabled: true, active: false, details: "x").level == .red)
        #expect(ServiceHealth.evaluate(installed: true, enabled: false, active: true, details: "x").level == .green)
    }

    @Test func describes_baker() {
        let conf = BakerConfig.content(nodeDataDir: "/d", nodeRpcPort: 8732, clientDir: "/c", keyAliases: ["consensus", "companion"],
                                       dalRpcPort: 10732, settings: BakerSettings(vote: .on))
        #expect(ServiceHealth.describeBaker(conf) == "Keys consensus, companion · LB vote on · with DAL")
    }
}

@Suite struct SettingsTests {
    @Test func archive_cannot_use_snapshot() {
        let s = NodeSettings(historyMode: .archive, importSnapshot: true)
        #expect(s.validate().contains { $0.contains("archive") })
    }

    @Test func ports_must_differ() {
        #expect(!NodeSettings(p2pPort: 9000, rpcPort: 9000).validate().isEmpty)
        #expect(NodeSettings().validate().isEmpty)
    }

    @Test func config_init_keeps_rpc_on_localhost() {
        let args = NodeSettings(network: .shadownet, rpcPort: 18732, allowAllRpc: true).configInitArgs(dataDir: "/d")
        #expect(args == ["config", "init", "--data-dir", "/d", "--network", "https://teztnets.com/shadownet", "--history-mode", "rolling",
                         "--net-addr", "0.0.0.0:9732", "--rpc-addr", "127.0.0.1:18732", "--allow-all-rpc", "127.0.0.1:18732"])
    }

    @Test func records_use_the_windows_json_keys() throws {
        let json = RecordJSON.encode(NodeSettingsRecord(NodeSettings(network: .bakingnet)))
        #expect(json.contains("\"Network\" : \"bakingnet\""))
        #expect(json.contains("\"P2PPort\" : 9732"))
        // As written by the Windows version.
        let windows = #"{"Network":"shadownet","HistoryMode":"rolling","P2PPort":9732,"RpcPort":8732,"AllowAllRpc":false}"#
        #expect(RecordJSON.decode(NodeSettingsRecord.self, windows)?.toSettings()?.network == .shadownet)
        let dal = RecordJSON.encode(DalSettingsRecord(attester: nil, rpcPort: 1, p2pPort: 2, nodeRpcPort: 3))
        #expect(dal.contains("\"Attester\" : null"))
        let keys = #"{"Setup":"ConsensusAndCompanion","Aliases":{"Consensus":"consensus","Companion":"companion"}}"#
        #expect(RecordJSON.decode(BakingKeysRecord.self, keys)?.orderedAliases == ["consensus", "companion"])
    }

    @Test func dal_validation() {
        let node = NodeSettings()
        #expect(DalSettings(enabled: true).validate(node: node, baking: true).contains { $0.contains("master key") })
        #expect(DalSettings(enabled: true).validate(node: node, baking: false).isEmpty)
        #expect(!DalSettings(enabled: true, rpcPort: 8732).validate(node: node, baking: false).isEmpty)
        #expect(!DalSettings(enabled: true, attesterAddress: "tz9nope").validate(node: node, baking: false).isEmpty)
    }

    @Test func mainnet_never_gets_a_master_key() {
        #expect(!BakingKeyChoice.isAllowed(.masterKey, on: .mainnet))
        #expect(!BakingKeyChoice.isAllowed(.masterAndCompanion, on: .mainnet))
        #expect(BakingKeyChoice.isAllowed(.consensusAndCompanion, on: .mainnet))
        #expect(BakingKeyChoice.isAllowed(.masterKey, on: .shadownet))
        #expect(throws: OctezDeskError.self) {
            try ProvisioningPlan.make(platform: FakePlatform(), node: NodeSettings(), keys: BakingKeyChoice(setup: .masterKey))
        }
    }

    @Test func funding() {
        #expect(FundingSettings(amount: 6001).stakeAmount == 6000)
        #expect(FundingSettings(amount: 100_000).stakeAmount == 10_000)
        #expect(!FundingSettings(amount: 5000).validate(faucetMax: nil).isEmpty)
        #expect(!FundingSettings(amount: 50_000).validate(faucetMax: 40_000).isEmpty)
        #expect(FundingSettings.applies(to: .bakingnet, keys: BakingKeyChoice(setup: .masterKey)))
        #expect(!FundingSettings.applies(to: .bakingnet, keys: BakingKeyChoice(setup: .consensusAndCompanion)))
    }

    @Test func tez_amounts() {
        #expect(TezAmount.format(6_000_000_000) == "6000")
        #expect(TezAmount.format(1_500_000) == "1.5")
        #expect(TezAmount.parse("1,000.5") == 1_000_500_000)
        #expect(TezAmount.parse("0.0000001") == nil)
        #expect(TezAmount.parse("abc") == nil)
        #expect(TezAmount.parse("0") == nil)
    }

    @Test func addresses() {
        #expect(isImplicitAddress("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"))
        #expect(!isImplicitAddress("tz5VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb"))
        #expect(!isImplicitAddress("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcj0"))
    }

    @Test func remote_signer_uri_round_trip() {
        let s = RemoteSigner(transport: .tcp, host: "192.168.1.20", port: 7732)
        #expect(RemoteSigner.parse(s.uri) == s)
        #expect(RemoteSigner.parse("https://signer.example.com")?.port == 443)
        #expect(!RemoteSigner(transport: .http, host: "bad host", port: 6732).validate().isEmpty)
    }
}

@Suite struct ConfigFileTests {
    @Test func parses_homebrew_conf() {
        let conf = """
            # comment
            DATADIR="$HOME/.tezos-node"
            RUNTIME_OPTS=""
            export LQ_VOTE='pass'
            KEYS=a b # trailing
            """
        let d = ShellConfigFile.parse(conf, home: "/Users/x")
        #expect(d["DATADIR"] == "/Users/x/.tezos-node")
        #expect(d["RUNTIME_OPTS"] == "")
        #expect(d["LQ_VOTE"] == "pass")
        #expect(d["KEYS"] == "a b")
    }

    @Test func quoting_round_trips() {
        let value = #"/Volumes/My "Disk"/$x"#
        let content = ShellConfigFile.setting("DATADIR", to: value, in: "# keep me\nDATADIR=\"$HOME/x\"\nOTHER=1\n")
        #expect(content.hasPrefix("# keep me\n"))
        #expect(content.contains("OTHER=1"))
        #expect(ShellConfigFile.parse(content)["DATADIR"] == value)
        #expect(ShellConfigFile.setting("NEW", to: "v", in: "A=1\n") == "A=1\nNEW=\"v\"\n")
    }

    @Test func baker_without_dal() {
        let conf = BakerConfig.content(nodeDataDir: "/d", nodeRpcPort: 8732, clientDir: "/c", keyAliases: ["baker"], dalRpcPort: 10732, settings: .init())
        let updated = BakerConfig.withoutDal(conf)
        #expect(updated.map { ShellConfigFile.parse($0)["DAL_OPTIONS"] } == "--without-dal")
        #expect(BakerConfig.withoutDal(updated!) == nil)
    }

    @Test func node_config() {
        let json = """
            { "rpc": { "listen-addrs": [ "127.0.0.1" ] }, "shell": { "history_mode": "rolling" },
              "network": { "chain_name": "TEZOS_USHUAIANET_2026-04-21T14:00:00Z" } }
            """
        #expect(NodeConfigFile.rpcPort(json) == 8732)
        #expect(NodeConfigFile.network(json) == .ushuaianet)
        #expect(NodeConfigFile.historyMode(json) == .rolling)
        #expect(NodeConfigFile.network(#"{"rpc":{"listen-addrs":["[::1]:9000"]}}"#) == .mainnet)
        #expect(NodeConfigFile.rpcPort(#"{"rpc":{"listen-addrs":["[::1]:9000"]}}"#) == 9000)
    }
}

@Suite struct FaucetTests {
    @Test func sha256_vectors() {
        #expect(SHA256.hex(SHA256.portableHash(Array("abc".utf8))) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        #expect(SHA256.hex(SHA256.portableHash([])) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        let long = Array(String(repeating: "a", count: 1000).utf8)
        #expect(SHA256.portableHash(long) == SHA256.hash(long))
    }

    @Test func solves_a_challenge() throws {
        let (nonce, solution) = try Faucet.solve(challenge: "test-challenge", difficulty: 3)
        #expect(nonce >= 1)
        #expect(solution.hasPrefix("000"))
        #expect(solution == SHA256.hex(SHA256.portableHash(Array("test-challenge:\(nonce)".utf8))))
    }

    @Test func leading_zeros() {
        #expect(Faucet.hasLeadingHexZeros([0x00, 0x0f], 3))
        #expect(!Faucet.hasLeadingHexZeros([0x00, 0x10], 3))
        #expect(Faucet.hasLeadingHexZeros([0x00, 0xff], 2))
    }
}

@Suite struct ProvisioningTests {
    /// A fake octez-node: `config init` writes a config.json for the chosen network.
    func platformWithOctez() -> FakePlatform {
        let p = FakePlatform()
        p.octez = { [unowned p] argv in
            if argv.starts(with: ["octez-node", "config", "init"]) || argv.starts(with: ["octez-node", "config", "update"]) {
                let dir = argv[argv.firstIndex(of: "--data-dir")! + 1]
                let rpc = argv[argv.firstIndex(of: "--rpc-addr")! + 1]
                let net = argv.firstIndex(of: "--network").map { argv[$0 + 1] } ?? "mainnet"
                p.files[dir + "/config.json"] = #"{"rpc":{"listen-addrs":["\#(rpc)"]},"network":"\#(net == "mainnet" ? "mainnet" : "shadownet")"}"#
            }
            return CommandResult(exitCode: 0)
        }
        return p
    }

    @Test func node_plan_runs_and_then_is_done() async throws {
        let p = platformWithOctez()
        let settings = NodeSettings(network: .mainnet, importSnapshot: false)
        let plan = try ProvisioningPlan.make(platform: p, node: settings)
        #expect(try await plan.run(progress: { _, _ in }, log: { _ in }))
        #expect(p.installed.isSuperset(of: [.node, .client]))
        #expect(p.states[.node] == ServiceState(enabled: true, active: true))
        #expect(await NodeService(p).recordedSettings() == NodeSettingsRecord(settings))
        for step in plan.steps { #expect(await step.isDone(), "\(step.title)") }
        #expect(await InstallationProbe.probe(p) == .ready)
    }

    @Test func never_replaces_a_node_it_did_not_set_up() async throws {
        let p = platformWithOctez()
        p.installed = [.node, .client]
        p.files["/home/u/.tezos-node/config.json"] = #"{"network":{"chain_name":"TEZOS_USHUAIANET_2026"}}"#
        let step = ConfigureNodeStep(p, node: NodeService(p), settings: NodeSettings(network: .mainnet))
        await #expect(throws: OctezDeskError.self) { try await step.run(log: { _ in }) }
        #expect(p.files["/home/u/.tezos-node/config.json"]!.contains("USHUAIANET"))
    }

    @Test func adopts_a_node_for_the_same_network() async throws {
        let p = platformWithOctez()
        let original = #"{"network":{"chain_name":"TEZOS_SHADOWNET_2025"},"rpc":{"listen-addrs":["0.0.0.0:8732"]}}"#
        p.files["/home/u/.tezos-node/config.json"] = original
        p.directories = ["/home/u/.tezos-node/store"]
        let step = ConfigureNodeStep(p, node: NodeService(p), settings: NodeSettings(network: .shadownet))
        try await step.run(log: { _ in })
        #expect(p.files["/home/u/.tezos-node/config.json.octezdesk-backup"] == original)
        #expect(p.calls.contains { $0.starts(with: ["octez-node", "config", "update"]) })
        #expect(!p.calls.contains { $0.starts(with: ["octez-node", "config", "init"]) })
        #expect(await step.isDone())
    }

    @Test func data_dir_step_changes_only_datadir() async throws {
        let p = FakePlatform()
        p.files["/etc/octez/octez-node.conf"] = "# hi\nDATADIR=\"$HOME/.tezos-node\"\nRUNTIME_OPTS=\"--x\"\n"
        let step = SetDataDirStep(p, service: .node, dataDir: "/Volumes/Big/tezos-node")
        #expect(!(await step.isDone()))
        try await step.run(log: { _ in })
        #expect(await step.isDone())
        #expect(p.files["/etc/octez/octez-node.conf"]!.contains("RUNTIME_OPTS=\"--x\""))
        #expect(await NodeService(p).dataDir() == "/Volumes/Big/tezos-node")
        #expect(await SetDataDirStep(FakePlatform(), service: .node, dataDir: "/home/u/.tezos-node/").isDone())
    }

    @Test func baker_config_backs_up_the_users_file() async throws {
        let p = FakePlatform()
        p.files["/etc/octez/octez-baker.conf"] = "KEYS=\"mine\"\n"
        p.files["/state/.octezdesk-keys.json"] = #"{"Setup":"MasterKey","Aliases":{"Master":"baker"}}"#
        let step = ConfigureBakerStep(p, keys: BakingKeyService(p), dal: nil, settings: BakerSettings(vote: .off))
        try await step.run(log: { _ in })
        #expect(p.files["/etc/octez/octez-baker.conf.octezdesk-backup"] == "KEYS=\"mine\"\n")
        let d = ShellConfigFile.parse(p.files["/etc/octez/octez-baker.conf"]!)
        #expect(d["KEYS"] == "baker")
        #expect(d["LQ_VOTE"] == "off")
        #expect(d["DAL_OPTIONS"] == "--without-dal")
        #expect(d["CLIENTDIR"] == "/home/u/.tezos-client")
        #expect(await step.isDone())
    }

    @Test func turning_off_dal_switches_the_baker_to_no_dal() async throws {
        let p = FakePlatform()
        p.states[.dalNode] = ServiceState(enabled: true, active: true)
        p.files["/etc/octez/octez-baker.conf"] = BakerConfig.content(nodeDataDir: "/d", nodeRpcPort: 8732, clientDir: "/c",
                                                                     keyAliases: ["baker"], dalRpcPort: 10732, settings: .init())
        try await ServiceControl.turnOff(p, .dalNode)
        #expect(p.states[.dalNode] == ServiceState(enabled: false, active: false))
        #expect(ShellConfigFile.parse(p.files["/etc/octez/octez-baker.conf"]!)["DAL_OPTIONS"] == "--without-dal")
    }
}
