// swift-tools-version: 6.0
// What the Mac and iPhone apps share: the API client, signing in, live sync and the rules for showing time.
import PackageDescription

let package = Package(
    name: "SpindleKit",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .library(name: "SpindleKit", targets: ["SpindleKit"]),
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.8.0"),
        .package(url: "https://github.com/apple/swift-openapi-urlsession", from: "1.1.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.3.0"),
    ],
    targets: [
        // Generated from openapi/spindle.json by scripts/generate-api.sh; not edited by hand.
        .target(
            name: "SpindleAPI",
            dependencies: [.product(name: "OpenAPIRuntime", package: "swift-openapi-runtime")],
            exclude: ["openapi-generator-config.yaml"],
            // Its warnings are the generator's to fix, not ours.
            swiftSettings: [.unsafeFlags(["-suppress-warnings"])]
        ),
        .target(
            name: "SpindleKit",
            dependencies: [
                "SpindleAPI",
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "OpenAPIURLSession", package: "swift-openapi-urlsession"),
                .product(name: "HTTPTypes", package: "swift-http-types"),
            ]
        ),
        .testTarget(name: "SpindleKitTests", dependencies: ["SpindleKit"]),
    ]
)
