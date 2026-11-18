import AppKit
@preconcurrency import ScreenCaptureKit

enum ScreenshotError: LocalizedError {
    case unavailable(String)

    var errorDescription: String? {
        switch self {
        case .unavailable(let reason):
            "Could not capture the screen: \(reason). Make sure Screen & System Audio Recording is allowed for Meetco."
        }
    }
}

/// Captures a downscaled JPEG of the main display (excluding Meetco's own windows)
/// to give the assistant optional visual context, e.g. a shared slide.
struct ScreenshotService: ScreenContextCapturing {
    var maxDimension = 1600

    func captureMainDisplay() async throws -> ScreenContext {
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            throw ScreenshotError.unavailable(error.localizedDescription)
        }
        let mainID = CGMainDisplayID()
        guard let display = content.displays.first(where: { $0.displayID == mainID }) ?? content.displays.first else {
            throw ScreenshotError.unavailable("no display")
        }
        let ownBundleID = Bundle.main.bundleIdentifier
        let ownApps = content.applications.filter { $0.bundleIdentifier == ownBundleID }
        let filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])

        let scale = min(1, Double(maxDimension) / Double(max(display.width, display.height)))
        let configuration = SCStreamConfiguration()
        configuration.width = max(2, Int(Double(display.width) * scale))
        configuration.height = max(2, Int(Double(display.height) * scale))
        configuration.showsCursor = false

        let image: CGImage
        do {
            image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
        } catch {
            throw ScreenshotError.unavailable(error.localizedDescription)
        }
        let representation = NSBitmapImageRep(cgImage: image)
        guard let jpeg = representation.representation(using: .jpeg, properties: [.compressionFactor: 0.7]) else {
            throw ScreenshotError.unavailable("JPEG encoding failed")
        }
        return ScreenContext(jpegData: jpeg, capturedAt: Date(), pixelWidth: image.width, pixelHeight: image.height)
    }
}
