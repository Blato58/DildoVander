import Capacitor

class KufrBridgeViewController: CAPBridgeViewController {
    override func capacitorDidLoad() {
        bridge?.registerPluginInstance(KufrDeviceTransportPlugin())
        bridge?.registerPluginInstance(KufrDebugPlugin())
        bridge?.registerPluginInstance(KufrProvisioningPlugin())
        if #available(iOS 16.0, *) { bridge?.registerPluginInstance(KufrIdentityPlugin()) }
    }
}
