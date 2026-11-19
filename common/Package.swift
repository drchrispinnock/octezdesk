// swift-tools-version:6.0
// OctezDeskCore: the code shared by the macOS and Linux versions of OctezDesk.
// Foundation only, so it builds with swift-corelibs-foundation on Linux.
import PackageDescription

let package = Package(
    name: "OctezDeskCore",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "OctezDeskCore", targets: ["OctezDeskCore"]),
    ],
    targets: [
        .target(name: "OctezDeskCore"),
        .testTarget(name: "OctezDeskCoreTests", dependencies: ["OctezDeskCore"]),
    ],
    swiftLanguageModes: [.v5]
)
