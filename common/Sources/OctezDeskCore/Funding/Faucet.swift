import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

/// What a faucet reports at `/info`.
public struct FaucetInfo: Sendable, Equatable {
    public var challengesEnabled: Bool
    public var minTez: Int
    public var maxTez: Int
}

/// Client for the teztnets faucets (github.com/tacoinfra/tezos-faucet-backend). Unless challenges are
/// off, the faucet hands out proof-of-work challenges: find a nonce such that SHA-256 of
/// "challenge:nonce", in hex, starts with `difficulty` zeros.
public struct Faucet: Sendable {
    public let baseURL: String

    public init(baseURL: String) { self.baseURL = baseURL.hasSuffix("/") ? String(baseURL.dropLast()) : baseURL }

    public static func `for`(_ network: TezosNetwork) throws -> Faucet {
        guard let url = network.faucetURL else { throw OctezDeskError("\(network.displayName) has no faucet.") }
        return Faucet(baseURL: url)
    }

    public func info() async throws -> FaucetInfo {
        let r = try await HTTP.get(baseURL + "/info", timeout: 15)
        guard (200..<300).contains(r.status), let o = (try? JSONSerialization.jsonObject(with: r.body)) as? [String: Any] else {
            throw OctezDeskError("The faucet at \(baseURL) didn't answer (HTTP \(r.status)).")
        }
        return FaucetInfo(challengesEnabled: o["challengesEnabled"] as? Bool ?? true,
                          minTez: o["minTez"] as? Int ?? 1, maxTez: o["maxTez"] as? Int ?? 0)
    }

    /// Requests `amount` ꜩ for `address`, solving the challenges. Returns the transfer's operation hash.
    public func request(address: String, amount: Int, log: @escaping LogSink) async throws -> String {
        let info = try await info()
        if !info.challengesEnabled {
            if let hash = try await post("verify", ["address": address, "amount": amount])["txHash"] as? String { return hash }
            throw OctezDeskError("The faucet did not send the tez.")
        }
        var r = try await post("challenge", ["address": address, "amount": amount])
        while true {
            try Task.checkCancellation()
            if let hash = r["txHash"] as? String { return hash }
            guard let challenge = r["challenge"] as? String, let difficulty = r["difficulty"] as? Int else {
                throw OctezDeskError("Unexpected answer from the faucet: \(r["message"] as? String ?? "no challenge")")
            }
            let counter = r["challengeCounter"] as? Int ?? 1
            let needed = r["challengesNeeded"] as? Int ?? counter
            if counter == 1 || counter % 10 == 0 || counter == needed {
                log("Solving the faucet's proof-of-work challenge \(counter) of \(needed)…")
            }
            let (nonce, solution) = try Self.solve(challenge: challenge, difficulty: difficulty)
            r = try await post("verify", ["address": address, "nonce": nonce, "solution": solution])
        }
    }

    /// Finds a nonce (from 1: the faucet rejects 0) whose hash has `difficulty` leading hex zeros.
    public static func solve(challenge: String, difficulty: Int) throws -> (nonce: Int, solution: String) {
        let prefix = Array((challenge + ":").utf8)
        var nonce = 1
        while true {
            if nonce % 4096 == 0 { try Task.checkCancellation() }
            let hash = SHA256.hash(prefix + Array(String(nonce).utf8))
            if hasLeadingHexZeros(hash, difficulty) { return (nonce, SHA256.hex(hash)) }
            nonce += 1
        }
    }

    public static func hasLeadingHexZeros(_ hash: [UInt8], _ zeros: Int) -> Bool {
        for i in 0..<(zeros / 2) where hash[i] != 0 { return false }
        return zeros % 2 == 0 || hash[zeros / 2] < 0x10
    }

    private func post(_ path: String, _ body: [String: Any]) async throws -> [String: Any] {
        let r = try await HTTP.postJSON(baseURL + "/" + path, body)
        let o = (try? JSONSerialization.jsonObject(with: r.body)) as? [String: Any]
        guard (200..<300).contains(r.status), let o, (o["status"] as? String) != "ERROR" else {
            throw OctezDeskError("The faucet refused the request: \(o?["message"] as? String ?? "HTTP \(r.status)")")
        }
        return o
    }
}

/// SHA-256 in pure Swift, so the shared code needs no crypto library on Linux.
public enum SHA256 {
    private static let k: [UInt32] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ]

    public static func hash(_ message: [UInt8]) -> [UInt8] {
        #if canImport(CryptoKit)
        return Array(CryptoKit.SHA256.hash(data: message))
        #else
        return portableHash(message)
        #endif
    }

    /// The pure Swift version, used where CryptoKit isn't available (Linux), and tested on every platform.
    public static func portableHash(_ message: [UInt8]) -> [UInt8] {
        var h: [UInt32] = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]
        var data = message
        let bitLength = UInt64(message.count) * 8
        data.append(0x80)
        while data.count % 64 != 56 { data.append(0) }
        for i in (0..<8).reversed() { data.append(UInt8((bitLength >> (UInt64(i) * 8)) & 0xff)) }

        var w = [UInt32](repeating: 0, count: 64)
        for chunk in stride(from: 0, to: data.count, by: 64) {
            for i in 0..<16 {
                let j = chunk + i * 4
                w[i] = UInt32(data[j]) << 24 | UInt32(data[j + 1]) << 16 | UInt32(data[j + 2]) << 8 | UInt32(data[j + 3])
            }
            for i in 16..<64 {
                let s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3)
                let s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10)
                w[i] = w[i - 16] &+ s0 &+ w[i - 7] &+ s1
            }
            var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7]
            for i in 0..<64 {
                let s1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)
                let ch = (e & f) ^ (~e & g)
                let t1 = hh &+ s1 &+ ch &+ k[i] &+ w[i]
                let s0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)
                let maj = (a & b) ^ (a & c) ^ (b & c)
                let t2 = s0 &+ maj
                hh = g; g = f; f = e; e = d &+ t1; d = c; c = b; b = a; a = t1 &+ t2
            }
            h[0] = h[0] &+ a; h[1] = h[1] &+ b; h[2] = h[2] &+ c; h[3] = h[3] &+ d
            h[4] = h[4] &+ e; h[5] = h[5] &+ f; h[6] = h[6] &+ g; h[7] = h[7] &+ hh
        }
        return h.flatMap { v in [UInt8(v >> 24), UInt8(v >> 16 & 0xff), UInt8(v >> 8 & 0xff), UInt8(v & 0xff)] }
    }

    public static func hex(_ bytes: [UInt8]) -> String { bytes.map { String(format: "%02x", $0) }.joined() }

    @inline(__always) private static func rotr(_ x: UInt32, _ n: UInt32) -> UInt32 { x >> n | x << (32 - n) }
}
