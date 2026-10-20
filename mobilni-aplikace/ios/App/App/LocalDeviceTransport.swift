import Foundation
import Security

// The only failure information allowed across the native/JavaScript boundary.
// NSError descriptions, URLs, certificate data and credentials stay native.
enum LocalDeviceTransportFailureCode: String, CaseIterable, Hashable, Sendable {
    case invalidIdentity = "invalid_identity"
    case certificateMismatch = "certificate_mismatch"
    case authorizationRejected = "authorization_rejected"
    case serverBusy = "server_busy"
    case handshakeRejected = "handshake_rejected"
    case tlsFailed = "tls_failed"
    case hostUnreachable = "host_unreachable"
    case networkUnavailable = "network_unavailable"
    case connectionTimeout = "connection_timeout"
    case protocolError = "protocol_error"
    case connectionLost = "connection_lost"
}

// All state and URLSession callbacks live on the main queue. Each operation
// owns its immutable pin; a late callback can never borrow a new connection.
final class LocalDeviceTransport: NSObject, URLSessionWebSocketDelegate, URLSessionTaskDelegate, @unchecked Sendable {
    let host: String
    let pin: String
    private let port: Int
    private var session: URLSession?
    private var socket: URLSessionWebSocketTask?
    private var pairingTask: URLSessionDataTask?
    private var pairingBytes = Data()
    private var pairingCompletion: ((Result<String, Error>) -> Void)?
    private var finished = false
    private var sendsPending = 0
    private(set) var failureCode: LocalDeviceTransportFailureCode?
    var onOpen: (() -> Void)?
    var onFrame: ((String) -> Void)?
    var onClose: (() -> Void)?

    static func classifyFailure(_ error: Error?, httpStatus: Int? = nil) -> LocalDeviceTransportFailureCode {
        if let httpStatus, httpStatus != 101 {
            switch httpStatus {
            case 401, 403: return .authorizationRejected
            case 409, 503: return .serverBusy
            default: return .handshakeRejected
            }
        }
        guard let error = error as NSError?, error.domain == NSURLErrorDomain else {
            return .connectionLost
        }
        switch error.code {
        case NSURLErrorBadURL, NSURLErrorUnsupportedURL:
            return .invalidIdentity
        case NSURLErrorUserAuthenticationRequired:
            return .authorizationRejected
        case NSURLErrorBadServerResponse:
            return .handshakeRejected
        case NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateHasBadDate,
             NSURLErrorServerCertificateUntrusted, NSURLErrorServerCertificateHasUnknownRoot,
             NSURLErrorServerCertificateNotYetValid, NSURLErrorClientCertificateRejected,
             NSURLErrorClientCertificateRequired, NSURLErrorAppTransportSecurityRequiresSecureConnection:
            return .tlsFailed
        case NSURLErrorCannotFindHost, NSURLErrorDNSLookupFailed, NSURLErrorCannotConnectToHost:
            return .hostUnreachable
        case NSURLErrorNotConnectedToInternet, NSURLErrorInternationalRoamingOff,
             NSURLErrorDataNotAllowed, NSURLErrorCallIsActive:
            return .networkUnavailable
        case NSURLErrorTimedOut:
            return .connectionTimeout
        case NSURLErrorDataLengthExceedsMaximum, NSURLErrorCannotDecodeRawData, NSURLErrorCannotDecodeContentData:
            return .protocolError
        default:
            return .connectionLost
        }
    }

    private func fail(_ code: LocalDeviceTransportFailureCode) {
        guard !finished else { return }
        failureCode = failureCode ?? code
        close()
    }

    private func fail(_ error: Error?, task: URLSessionTask?) {
        fail(Self.classifyFailure(error, httpStatus: (task?.response as? HTTPURLResponse)?.statusCode))
    }

    init(host: String, pin: String, port: Int = 443) {
        self.host = host
        self.pin = pin
        self.port = port
        super.init()
    }

    private func startSession() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 15
        configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: .main)
    }

    private func url(_ scheme: String, _ path: String) -> URL {
        var components = URLComponents()
        components.scheme = scheme
        components.host = host
        components.port = port
        components.path = path
        return components.url!
    }

    func pair(secret: String, completion: @escaping (Result<String, Error>) -> Void) {
        guard session == nil, !finished,
              LocalDeviceIdentity.validHost(host), LocalDeviceIdentity.validHex(pin, bytes: 32),
              secret.utf8.count >= 32, secret.utf8.count <= 128,
              secret.utf8.count % 2 == 0,
              LocalDeviceIdentity.validHex(secret, bytes: secret.utf8.count / 2) else {
            if !finished { failureCode = failureCode ?? .invalidIdentity }
            completion(.failure(LocalDeviceError.invalidIdentity))
            return
        }
        startSession()
        pairingCompletion = completion
        var request = URLRequest(url: url("https", "/v1/pair"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "protocolVersion": 1, "pairingSecret": secret
        ])
        pairingTask = session!.dataTask(with: request)
        pairingTask!.resume()
    }

    func connect(token: String) {
        guard session == nil, !finished,
              LocalDeviceIdentity.validHost(host), LocalDeviceIdentity.validHex(pin, bytes: 32),
              LocalDeviceIdentity.validHex(token, bytes: 32) else {
            fail(.invalidIdentity)
            return
        }
        startSession()
        var request = URLRequest(url: url("wss", "/v1/control"))
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        socket = session!.webSocketTask(with: request)
        socket!.maximumMessageSize = 8192
        socket!.resume()
    }

    func send(_ frame: String, completion: @escaping (Bool) -> Void) {
        guard !finished, let socket, frame.utf8.count <= 8192, sendsPending < 4 else {
            fail(.protocolError)
            completion(false)
            return
        }
        sendsPending += 1
        socket.send(.string(frame)) { [weak self] error in
            DispatchQueue.main.async {
                guard let self, !self.finished else { completion(false); return }
                self.sendsPending -= 1
                if let error { self.fail(error, task: socket) }
                completion(error == nil)
            }
        }
    }

    func close() {
        guard !finished else { return }
        finished = true
        let completion = pairingCompletion
        pairingCompletion = nil
        pairingBytes.resetBytes(in: 0..<pairingBytes.count)
        pairingBytes.removeAll()
        pairingTask?.cancel()
        socket?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        session = nil
        socket = nil
        pairingTask = nil
        completion?(.failure(LocalDeviceError.invalidPairingResponse))
        onClose?()
    }

    private func receive() {
        socket?.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, !self.finished else { return }
                switch result {
                case .success(.string(let frame)) where frame.utf8.count <= 8192:
                    self.onFrame?(frame)
                    self.receive()
                case .success:
                    self.fail(.protocolError)
                case .failure(let error):
                    self.fail(error, task: self.socket)
                }
            }
        }
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard !finished, session === self.session else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            fail(.tlsFailed)
            return
        }
        guard
              challenge.protectionSpace.host.caseInsensitiveCompare(host) == .orderedSame,
              challenge.protectionSpace.port == port,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first,
              LocalCertificatePin.matches(SecCertificateCopyData(leaf) as Data, expected: pin) else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            fail(.certificateMismatch)
            return
        }
        // This isolated LAN session uses the physically obtained QR/BLE SPKI as its
        // authority, including self-signed identity. Cloud traffic never uses it.
        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
        guard !finished, session === self.session,
              task === socket || task === pairingTask else { return }
        fail(.handshakeRejected)
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didOpenWithProtocol selectedProtocol: String?) {
        guard !finished, session === self.session, webSocketTask === socket else { return }
        guard selectedProtocol == nil else { fail(.protocolError); return }
        onOpen?()
        receive()
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask,
                    didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        guard !finished, session === self.session, webSocketTask === socket else { return }
        fail(closeCode == .protocolError || closeCode == .messageTooBig ? .protocolError : .connectionLost)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard !finished, session === self.session else { return }
        if task === pairingTask {
            let completion = pairingCompletion
            pairingCompletion = nil
            let response = task.response as? HTTPURLResponse
            let result: Result<String, Error>
            if error == nil, response?.statusCode == 200,
               response?.mimeType == "application/json",
               let object = try? JSONSerialization.jsonObject(with: pairingBytes) as? [String: Any],
               Set(object.keys) == Set(["protocolVersion", "token"]),
               let version = object["protocolVersion"] as? NSNumber,
               CFGetTypeID(version) != CFBooleanGetTypeID(), version == 1,
               let token = object["token"] as? String,
               LocalDeviceIdentity.validHex(token, bytes: 32) {
                result = .success(token)
            } else {
                failureCode = error == nil && response?.statusCode == 200 ? .protocolError :
                    Self.classifyFailure(error, httpStatus: response?.statusCode == 200 ? nil : response?.statusCode)
                result = .failure(LocalDeviceError.invalidPairingResponse)
            }
            close()
            completion?(result)
        } else if task === socket {
            fail(error, task: task)
        }
    }
}

extension LocalDeviceTransport: URLSessionDataDelegate {
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard !finished, session === self.session, dataTask === pairingTask else {
            completionHandler(.cancel)
            return
        }
        guard
              let response = response as? HTTPURLResponse,
              response.statusCode == 200, response.mimeType == "application/json",
              response.expectedContentLength <= 192 else {
            completionHandler(.cancel)
            let status = (response as? HTTPURLResponse)?.statusCode
            fail(status == 200 ? .protocolError : Self.classifyFailure(nil, httpStatus: status))
            return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard !finished, session === self.session, dataTask === pairingTask else { return }
        guard data.count <= 192 - pairingBytes.count else {
            fail(.protocolError)
            return
        }
        pairingBytes.append(data)
    }
}
