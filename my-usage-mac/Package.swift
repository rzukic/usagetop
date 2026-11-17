// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AiUsageMac",
    platforms: [.macOS("26.0")],
    targets: [
        .executableTarget(
            name: "AiUsageMac",
            path: "Sources/AiUsageMac"
        )
    ]
)
