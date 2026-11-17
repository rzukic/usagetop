import AppKit

// A menu bar app has no window most of the time, so AppKit is free to reap it the
// moment the panel closes. The opt-outs below stop the system terminating an idle
// app, and the delegate refuses to treat the panel closing as the last window.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var status: StatusController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        ProcessInfo.processInfo.disableAutomaticTermination("usagetop lives in the menu bar")
        ProcessInfo.processInfo.disableSuddenTermination()

        let controller = StatusController(model: AppModel())
        controller.start()
        status = controller
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }
}

// Top-level code runs on the main thread, so the main actor is already the
// current one; assumeIsolated says so to the compiler.
MainActor.assumeIsolated {
    let application = NSApplication.shared
    let delegate = AppDelegate()
    application.delegate = delegate
    application.setActivationPolicy(.accessory)
    application.run()
}
