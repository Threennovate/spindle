// Ed25519 keys and signatures as Sparkle writes them, for release-mac.sh:
//
//   swift ed25519.swift new <private-key-file>                     writes a new private key, prints its public key
//   swift ed25519.swift public <private-key-file>                  prints the public key of a private key
//   swift ed25519.swift verify <public-key> <signature> <file>     checks that the key signed the file
//
// A private key file holds the key's 32-byte seed in base64, as Sparkle's `generate_keys -x` exports it; a public key
// and a signature are base64 too, as in SUPublicEDKey and an appcast's sparkle:edSignature.
import CryptoKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

func privateKey(at path: String) -> Curve25519.Signing.PrivateKey {
    guard let text = try? String(contentsOfFile: path, encoding: .utf8),
          let seed = Data(base64Encoded: text.trimmingCharacters(in: .whitespacesAndNewlines)),
          let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: seed)
    else { fail("\(path) isn't a private key: expected a 32-byte seed in base64.") }
    return key
}

let arguments = CommandLine.arguments.dropFirst()
switch (arguments.first, arguments.count) {
case ("new", 2):
    let path = arguments.last!
    let key = Curve25519.Signing.PrivateKey()
    FileManager.default.createFile(atPath: path, contents: Data(key.rawRepresentation.base64EncodedString().utf8), attributes: [.posixPermissions: 0o600])
    print(key.publicKey.rawRepresentation.base64EncodedString())
case ("public", 2):
    print(privateKey(at: arguments.last!).publicKey.rawRepresentation.base64EncodedString())
case ("verify", 4):
    let values = Array(arguments.dropFirst())
    guard let publicKey = Data(base64Encoded: values[0]).flatMap({ try? Curve25519.Signing.PublicKey(rawRepresentation: $0) }) else {
        fail("The public key isn't a base64 Ed25519 key.")
    }
    guard let signature = Data(base64Encoded: values[1]) else { fail("The signature isn't base64.") }
    guard let file = FileManager.default.contents(atPath: values[2]) else { fail("Can't read \(values[2]).") }
    guard publicKey.isValidSignature(signature, for: file) else { fail("The signature doesn't match the file and key.") }
default:
    fail("Usage: swift ed25519.swift new <key-file> | public <key-file> | verify <public-key> <signature> <file>")
}
