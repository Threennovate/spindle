import Foundation

extension Bundle {
    /// "1.2.0 (15)": the version people see, and the build number updates are compared by.
    var version: String {
        let short = object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let build = object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        return "\(short) (\(build))"
    }
}
