import AppKit

// A menu bar app has no window most of the time, so AppKit is free to reap it the
// moment the panel closes. The opt-outs below stop the system terminating an idle
// app, and the delegate refuses to treat the panel closing as the last window.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var status: StatusController?
    private var updater: Updater?

    func applicationDidFinishLaunching(_ notification: Notification) {
        ProcessInfo.processInfo.disableAutomaticTermination("usagetop lives in the menu bar")
        ProcessInfo.processInfo.disableSuddenTermination()

        let model = AppModel()
        // Not while drawing screenshots: those runs quit by themselves.
        let drawing = CommandLine.arguments.contains("--snapshot") || CommandLine.arguments.contains("--showcase")
        if !drawing {
            // An update an earlier run downloaded goes in before anything is
            // shown, and the new version takes over.
            if model.updater.start() {
                NSApplication.shared.terminate(nil)
                return
            }
            updater = model.updater
        }

        let controller = StatusController(model: model)
        controller.start()
        status = controller
    }

    /// A downloaded update goes in as the app leaves.
    func applicationWillTerminate(_ notification: Notification) {
        updater?.installOnQuit()
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
