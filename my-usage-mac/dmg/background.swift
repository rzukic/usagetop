// Draws the background of the DMG window: the icon's dark plate, and between
// the app and Applications an arrow drawn as one of the icon's meters, filled
// towards Applications.
//
//   swift background.swift <out.png> <scale>
//
// The window is 660×400 points; make-dmg.sh draws it at 1× and 2× and joins
// the two into one TIFF, so it is sharp on every screen. Layout constants
// here and in make-dmg.sh must agree.
import AppKit

let args = CommandLine.arguments
guard args.count == 3, let scale = Double(args[2]) else {
    FileHandle.standardError.write("usage: swift background.swift <out.png> <scale>\n".data(using: .utf8)!)
    exit(1)
}
let w = 660.0, h = 400.0
let iconY = 190.0          // icon centres, from the top (make-dmg.sh)
let appX = 160.0, appsX = 500.0

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(w * scale), pixelsHigh: Int(h * scale),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
rep.size = NSSize(width: w, height: h)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let cg = NSGraphicsContext.current!.cgContext

func rgb(_ hex: UInt32, _ a: Double = 1) -> NSColor {
    NSColor(srgbRed: Double((hex >> 16) & 0xff) / 255, green: Double((hex >> 8) & 0xff) / 255,
            blue: Double(hex & 0xff) / 255, alpha: a)
}
// AppKit's origin is bottom left; the layout is measured from the top.
func top(_ y: Double) -> Double { h - y }

// The plate: the icon's diagonal charcoal, and a soft light from above.
NSGradient(colors: [rgb(0x26262c), rgb(0x141418)])!.draw(in: NSRect(x: 0, y: 0, width: w, height: h), angle: -60)
NSGradient(colors: [rgb(0x8fb4ea, 0.10), rgb(0x8fb4ea, 0)])!
    .draw(fromCenter: NSPoint(x: w / 2, y: top(-40)), radius: 0, toCenter: NSPoint(x: w / 2, y: top(-40)), radius: 360, options: [])

// Plates under the icons' names. Finder writes them black in light mode and
// white in dark mode; on this grey both can be read.
for x in [appX, appsX] {
    let plate = NSRect(x: x - 56, y: top(iconY + 64 + 25), width: 112, height: 22)
    rgb(0x77777f).setFill()
    NSBezierPath(roundedRect: plate, xRadius: 11, yRadius: 11).fill()
}

// The arrow: a meter's track from the app to Applications, filled most of the
// way, its marker standing where the fill ends, and a chevron at the end.
let a0 = appX + 84, a1 = appsX - 84, ay = top(iconY)
let end = a1 - 28
let track = NSRect(x: a0, y: ay - 8, width: end - a0, height: 16)
rgb(0xffffff, 0.08).setFill()
NSBezierPath(roundedRect: track, xRadius: 8, yRadius: 8).fill()
let filled = NSRect(x: a0, y: ay - 8, width: (end - a0) * 0.7, height: 16)
NSGradient(colors: [rgb(0x6a93cc), rgb(0x9cc0f0)])!.draw(in: NSBezierPath(roundedRect: filled, xRadius: 8, yRadius: 8), angle: 0)
rgb(0xf2f2f2).setFill()
NSBezierPath(roundedRect: NSRect(x: filled.maxX - 6, y: ay - 15, width: 6, height: 30), xRadius: 3, yRadius: 3).fill()
let chevron = NSBezierPath()
chevron.move(to: NSPoint(x: a1 - 12, y: ay + 9))
chevron.line(to: NSPoint(x: a1 - 3, y: ay))
chevron.line(to: NSPoint(x: a1 - 12, y: ay - 9))
chevron.lineWidth = 3.5
chevron.lineCapStyle = .round
chevron.lineJoinStyle = .round
rgb(0xffffff, 0.40).setStroke()
chevron.stroke()

// Words.
let para = NSMutableParagraphStyle()
para.alignment = .center
func text(_ s: String, _ y: Double, _ size: Double, _ weight: NSFont.Weight, _ color: NSColor, kern: Double = 0) {
    let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: weight),
                                                .foregroundColor: color, .paragraphStyle: para, .kern: kern]
    NSAttributedString(string: s, attributes: attrs).draw(in: NSRect(x: 0, y: top(y) - size * 1.3, width: w, height: size * 1.6))
}
text("usagetop", 34, 22, .semibold, rgb(0xf2f2f4), kern: 0.2)
text("Drag usagetop into Applications to install it.", 66, 13, .regular, rgb(0xa3a3ac))

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: args[1]))
