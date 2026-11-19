import Foundation

/// The service settings files (`/etc/default/octez-node`, Homebrew's `etc/octez/octez-node.conf`):
/// `KEY=value` lines, optionally quoted, `#` comments, an optional leading `export`. They're sourced
/// by a shell, so `$HOME` is expanded here; nothing else is.
public enum ShellConfigFile {
    /// The header OctezDesk puts on a settings file it writes in full.
    public static let managedHeader = "# Managed by OctezDesk. Changes here are replaced when you run its setup wizard."

    public static func parse(_ content: String, home: String? = nil) -> [String: String] {
        var result: [String: String] = [:]
        for raw in content.split(separator: "\n", omittingEmptySubsequences: false) {
            var line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            if line.hasPrefix("export ") { line = String(line.dropFirst(7)).trimmingCharacters(in: .whitespaces) }
            guard let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces)
            var value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            var expand = true
            if value.count >= 2, let q = value.first, q == "\"" || q == "'", value.last == q {
                value = String(value.dropFirst().dropLast())
                if q == "'" { expand = false } else { value = unescapeDoubleQuoted(value) }
            } else if let hash = value.range(of: " #") {
                value = String(value[..<hash.lowerBound]).trimmingCharacters(in: .whitespaces)
            }
            if expand, let home {
                value = value.replacingOccurrences(of: "${HOME}", with: home).replacingOccurrences(of: "$HOME", with: home)
                if value == "~" || value.hasPrefix("~/") { value = home + value.dropFirst() }
            }
            result[key] = value
        }
        return result
    }

    /// A value in double quotes, safe to source: `"`, `\`, `$` and `` ` `` are escaped.
    public static func quote(_ value: String) -> String {
        var s = "\""
        for c in value {
            if "\"\\$`".contains(c) { s.append("\\") }
            s.append(c)
        }
        return s + "\""
    }

    private static func unescapeDoubleQuoted(_ s: String) -> String {
        var out = ""
        var escaping = false
        for c in s {
            if escaping { out.append(c); escaping = false } else if c == "\\" { escaping = true } else { out.append(c) }
        }
        return out
    }

    /// `content` with `key` set to `value` (quoted), replacing an existing line or appending one. Other lines are kept.
    public static func setting(_ key: String, to value: String, in content: String) -> String {
        let newLine = "\(key)=\(quote(value))"
        var lines = content.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        if let i = lines.firstIndex(where: { l in
            var t = l.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("export ") { t = String(t.dropFirst(7)).trimmingCharacters(in: .whitespaces) }
            return t.hasPrefix(key + "=")
        }) {
            lines[i] = newLine
        } else {
            if lines.last == "" { lines.removeLast() }
            lines.append(newLine)
            lines.append("")
        }
        return lines.joined(separator: "\n")
    }

    public static func isManagedByOctezDesk(_ content: String) -> Bool {
        content.hasPrefix(managedHeader)
    }
}

/// Reads the parts of an Octez node's `config.json` that OctezDesk needs.
public enum NodeConfigFile {
    static func object(_ json: String) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8))) as? [String: Any]
    }

    /// All `rpc.listen-addrs` (or the older `listen-addr`); empty if none; nil if the JSON is invalid.
    public static func rpcListenAddrs(_ json: String) -> [String]? {
        guard let root = object(json) else { return nil }
        guard let rpc = root["rpc"] as? [String: Any] else { return [] }
        if let addrs = rpc["listen-addrs"] as? [String] { return addrs }
        if let single = rpc["listen-addr"] as? String { return [single] }
        return []
    }

    /// The port of the first RPC listen address, or nil. An address without a port means the default, 8732.
    public static func rpcPort(_ json: String) -> Int? {
        guard let addr = rpcListenAddrs(json)?.first else { return nil }
        return port(of: addr) ?? NodeSettings.defaultRpcPort
    }

    /// "127.0.0.1:8732" → 8732, "[::1]:8732" → 8732, "127.0.0.1" → nil.
    static func port(of addr: String) -> Int? {
        if addr.hasPrefix("[") {
            guard let close = addr.lastIndex(of: "]") else { return nil }
            let rest = addr[addr.index(after: close)...]
            return rest.hasPrefix(":") ? Int(rest.dropFirst()) : nil
        }
        let parts = addr.split(separator: ":")
        return parts.count == 2 ? Int(parts[1]) : nil
    }

    /// The node's history mode, if the config sets one (the default is full).
    public static func historyMode(_ json: String) -> HistoryMode? {
        guard let shell = object(json)?["shell"] as? [String: Any] else { return nil }
        if let mode = shell["history_mode"] as? String { return HistoryMode(rawValue: mode) }
        // {"rolling": {"additional_cycles": 1}}
        if let mode = shell["history_mode"] as? [String: Any], let key = mode.keys.first { return HistoryMode(rawValue: key) }
        return nil
    }

    /// Which of OctezDesk's networks the config is for, from `network` (an alias or the embedded
    /// chain parameters, whose `chain_name` is like `TEZOS_SHADOWNET_2025-…`). Nil if it's another one.
    public static func network(_ json: String) -> TezosNetwork? {
        guard let root = object(json) else { return nil }
        guard let network = root["network"] else { return .mainnet } // no "network": mainnet
        if let alias = network as? String { return TezosNetwork.withId(alias.lowercased()) }
        guard let chain = (network as? [String: Any])?["chain_name"] as? String else { return nil }
        let upper = chain.uppercased()
        if upper == "TEZOS_MAINNET" { return .mainnet }
        return TezosNetwork.all.first { !$0.isMainnet && upper.hasPrefix("TEZOS_\($0.id.uppercased())") }
    }
}
