#!/usr/bin/env swift
// Generates Meetco's app icon PNGs into the asset catalog.
// Usage (from macos/): swift scripts/generate-icon.swift
import AppKit

let output = URL(fileURLWithPath: "Meetco/Resources/Assets.xcassets/AppIcon.appiconset", isDirectory: true)

func render(size: Int) -> Data {
    let pixels = CGFloat(size)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    // macOS icon grid: 824/1024 body with ~185/1024 corner radius.
    let inset = pixels * 100 / 1024
    let body = NSRect(x: inset, y: inset, width: pixels - inset * 2, height: pixels - inset * 2)
    let radius = pixels * 185 / 1024
    let path = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)

    NSGraphicsContext.current?.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
    shadow.shadowBlurRadius = pixels * 0.02
    shadow.shadowOffset = NSSize(width: 0, height: -pixels * 0.008)
    shadow.set()
    NSColor(calibratedWhite: 0.1, alpha: 1).setFill()
    path.fill()
    NSGraphicsContext.current?.restoreGraphicsState()

    // Graphite glass gradient.
    NSGradient(colors: [NSColor(calibratedWhite: 0.26, alpha: 1), NSColor(calibratedWhite: 0.07, alpha: 1)])!
        .draw(in: path, angle: -90)
    // Top sheen.
    NSGraphicsContext.current?.saveGraphicsState()
    path.addClip()
    let sheen = NSBezierPath(ovalIn: NSRect(x: body.minX - body.width * 0.2, y: body.midY + body.height * 0.05,
                                            width: body.width * 1.4, height: body.height * 0.9))
    NSColor.white.withAlphaComponent(0.06).setFill()
    sheen.fill()
    NSGraphicsContext.current?.restoreGraphicsState()
    // Hairline border.
    NSColor.white.withAlphaComponent(0.14).setStroke()
    path.lineWidth = max(1, pixels / 512)
    path.stroke()

    // Waveform bars.
    let heights: [CGFloat] = [0.18, 0.36, 0.58, 0.40, 0.24]
    let barWidth = body.width * 0.075
    let gap = body.width * 0.055
    let totalWidth = CGFloat(heights.count) * barWidth + CGFloat(heights.count - 1) * gap
    var x = body.midX - totalWidth / 2
    NSColor(calibratedWhite: 0.96, alpha: 1).setFill()
    for factor in heights {
        let height = body.height * factor
        let bar = NSRect(x: x, y: body.midY - height / 2, width: barWidth, height: height)
        NSBezierPath(roundedRect: bar, xRadius: barWidth / 2, yRadius: barWidth / 2).fill()
        x += barWidth + gap
    }

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

var images: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try! render(size: pixels).write(to: output.appendingPathComponent(name))
        images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
    }
}
let contents: [String: Any] = ["images": images, "info": ["author": "xcode", "version": 1]]
let json = try! JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
try! json.write(to: output.appendingPathComponent("Contents.json"))
print("Generated \(images.count) icon images")
