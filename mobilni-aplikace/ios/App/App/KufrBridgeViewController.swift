import Capacitor
import UIKit
import WebKit

class KufrBridgeViewController: CAPBridgeViewController {
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .portrait }
    override var preferredInterfaceOrientationForPresentation: UIInterfaceOrientation { .portrait }

    // Apply only inside the native shell. Browser pages retain accessible page zoom.
    private static let viewportScript = """
    (() => {
        let viewport = document.querySelector('meta[name="viewport"]');
        if (!viewport) {
            viewport = document.createElement('meta');
            viewport.name = 'viewport';
            document.head.appendChild(viewport);
        }
        viewport.content = 'width=device-width, initial-scale=1, minimum-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover';
    })();
    """

    override func webView(with frame: CGRect, configuration: WKWebViewConfiguration) -> WKWebView {
        configuration.ignoresViewportScaleLimits = false
        configuration.userContentController.addUserScript(WKUserScript(
            source: Self.viewportScript, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        let view = super.webView(with: frame, configuration: configuration)
        view.scrollView.pinchGestureRecognizer?.isEnabled = false
        return view
    }

    override func capacitorDidLoad() {
        bridge?.registerPluginInstance(KufrDeviceTransportPlugin())
        bridge?.registerPluginInstance(KufrDebugPlugin())
        bridge?.registerPluginInstance(KufrProvisioningPlugin())
        bridge?.registerPluginInstance(KufrIdentityPlugin())
    }
}
