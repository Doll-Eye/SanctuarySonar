// swift-tools-version: 5.9
// Sanctuary Sonar (working title in code: Dungeon Guide): reads the Diablo IV dungeon map out of a streamed game window and turns it
// into sound. Stage 0 is the recorder — see DESIGN.md. Built into an app bundle by build.sh.
import PackageDescription

let package = Package(
    name: "DungeonGuide",
    platforms: [.macOS("27.0")],   // Keel's floor
    dependencies: [
        // Keel: the controller, speech, sound and log package shared with Muteny. Fetched
        // from GitHub so a clone of this repository builds on its own.
        .package(url: "https://github.com/Doll-Eye/Keel.git", branch: "main")
    ],
    targets: [
        .executableTarget(
            name: "DungeonGuide",
            dependencies: [.product(name: "Keel", package: "Keel"), "Cartography"],
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("Carbon")
            ]),
        // The map reader run offline over a recording; see Sources/MapLab/main.swift.
        .executableTarget(
            name: "MapLab",
            dependencies: ["Cartography"],
            linkerSettings: [.linkedFramework("AVFoundation")]),
        // Reading the minimap, stitching it into one map, and finding the way — shared by
        // the app (live) and MapLab (recordings).
        .target(name: "Cartography")
    ]
)
