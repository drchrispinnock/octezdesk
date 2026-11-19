// swift-tools-version:6.0
// OctezDesk for macOS: a SwiftUI app that runs Octez from the drchrispinnock/octez Homebrew tap.
// The shared logic is in ../common (OctezDeskCore); HomebrewPlatform is the macOS side of it.
import PackageDescription

let package = Package(
    name: "OctezDeskMac",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(path: "../common"),
    ],
    targets: [
        .target(name: "OctezDeskHomebrew", dependencies: [.product(name: "OctezDeskCore", package: "common")]),
        .executableTarget(name: "OctezDesk", dependencies: ["OctezDeskHomebrew", .product(name: "OctezDeskCore", package: "common")]),
        .testTarget(name: "OctezDeskHomebrewTests", dependencies: ["OctezDeskHomebrew"]),
    ],
    swiftLanguageModes: [.v5]
)
