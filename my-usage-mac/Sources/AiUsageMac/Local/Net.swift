import Foundation

/// HTTP and JSON for the providers this app asks itself, without a server.
/// The providers disagree about whether a number arrives as a number or as a
/// string, so every read goes through `JSON` rather than a cast.
enum Net {
    struct Reply {
        let code: Int
        let data: Data

        var json: [String: Any]? {
            (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        }

        var text: String { String(decoding: data, as: UTF8.self) }
    }

    private static let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.timeoutIntervalForResource = 30
        config.waitsForConnectivity = false
        return URLSession(configuration: config)
    }()

    static func get(_ url: String, headers: [String: String] = [:]) async throws -> Reply {
        try await send(url, method: "GET", headers: headers, body: nil)
    }

    static func postJSON(_ url: String, headers: [String: String] = [:], body: String) async throws -> Reply {
        var headers = headers
        headers["Content-Type"] = "application/json"
        return try await send(url, method: "POST", headers: headers, body: Data(body.utf8))
    }

    static func postJSON(_ url: String, headers: [String: String] = [:], object: [String: Any]) async throws -> Reply {
        let body = try JSONSerialization.data(withJSONObject: object)
        return try await postJSON(url, headers: headers, body: String(decoding: body, as: UTF8.self))
    }

    static func postForm(_ url: String, headers: [String: String] = [:], fields: [(String, String)]) async throws -> Reply {
        var headers = headers
        headers["Content-Type"] = "application/x-www-form-urlencoded"
        return try await send(url, method: "POST", headers: headers, body: Data(form(fields).utf8))
    }

    static func form(_ fields: [(String, String)]) -> String {
        fields.map { "\($0.0)=\(encode($0.1))" }.joined(separator: "&")
    }

    static func encode(_ value: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
    }

    private static func send(_ url: String, method: String, headers: [String: String], body: Data?) async throws -> Reply {
        guard let target = URL(string: url) else { throw URLError(.badURL) }
        var request = URLRequest(url: target)
        request.httpMethod = method
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        request.httpBody = body
        let (data, response) = try await session.data(for: request)
        return Reply(code: (response as? HTTPURLResponse)?.statusCode ?? 0, data: data)
    }
}

enum JSON {
    static func object(_ json: [String: Any]?, _ name: String) -> [String: Any]? {
        json?[name] as? [String: Any]
    }

    static func array(_ json: [String: Any]?, _ name: String) -> [[String: Any]] {
        (json?[name] as? [Any])?.compactMap { $0 as? [String: Any] } ?? []
    }

    /// A string, or a number written as one; nil when absent or empty.
    static func text(_ json: [String: Any]?, _ name: String) -> String? {
        switch json?[name] {
        case let text as String: text.isEmpty ? nil : text
        case let number as NSNumber where !(number === kCFBooleanTrue || number === kCFBooleanFalse): number.stringValue
        default: nil
        }
    }

    /// A number, or a string holding one; nil when absent or not a number.
    static func number(_ json: [String: Any]?, _ name: String) -> Double? {
        switch json?[name] {
        case let number as NSNumber where !(number === kCFBooleanTrue || number === kCFBooleanFalse): number.doubleValue
        case let text as String: Double(text.trimmingCharacters(in: .whitespaces))
        default: nil
        }
    }

    static func flag(_ json: [String: Any]?, _ name: String) -> Bool {
        (json?[name] as? NSNumber)?.boolValue ?? false
    }

    /// An ISO timestamp, with or without fractions or an offset.
    static func date(_ text: String?) -> Date? {
        guard let text, !text.isEmpty else { return nil }
        if let date = WireDate.parse(text) { return date }
        // "2026-11-19T19:33:54" with no zone is read as UTC, as the providers mean it.
        return WireDate.parse(text + "Z")
    }
}
