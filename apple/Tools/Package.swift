// swift-tools-version: 6.0
// Tools used while developing the Apple apps; not part of them.
import PackageDescription

let package = Package(
    name: "Tools",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/apple/swift-openapi-generator", exact: "1.13.1"),
    ]
)
