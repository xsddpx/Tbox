import Foundation
import CoreGraphics
import CoreText
import ImageIO

// Render only a synthetic in-memory image. No windows, input, screen capture,
// account data or privacy permission requests are used by this integration test.
@main struct OcrMetadataTests {
    static func fixture(blank: Bool = false) -> Data {
        let context = CGContext(data: nil, width: 1000, height: 600, bitsPerComponent: 8, bytesPerRow: 4000,
                                space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(gray: 1, alpha: 1)); context.fill(CGRect(x: 0, y: 0, width: 1000, height: 600))
        if !blank {
            for (text, y, name) in [("TBOX OCR", 470.0, "Helvetica"), ("Shared words 123", 290.0, "Helvetica"),
                                    ("中文识别", 110.0, "PingFangSC-Regular")] {
                let font = CTFontCreateWithName(name as CFString, 64, nil)
                let string = NSAttributedString(string: text, attributes: [
                    NSAttributedString.Key(kCTFontAttributeName as String): font,
                    NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 0, alpha: 1)])
                context.textPosition = CGPoint(x: 60, y: y)
                CTLineDraw(CTLineCreateWithAttributedString(string), context)
            }
        }
        let output = NSMutableData()
        let destination = CGImageDestinationCreateWithData(output, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        precondition(CGImageDestinationFinalize(destination))
        return output as Data
    }

    static func run(_ data: Data) throws -> [String: Any] {
        let pointer = data.withUnsafeBytes { recognize($0.baseAddress, Int32($0.count)) }
        guard let pointer = pointer else { fatalError("Native OCR did not return JSON") }
        defer { freeResponse(UnsafeMutableRawPointer(pointer)) }
        return try JSONSerialization.jsonObject(with: Data(String(cString: pointer).utf8)) as! [String: Any]
    }

    enum Failure: Error { case invalid(String) }
    static func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        if !condition() { throw Failure.invalid(message) }
    }

    static func main() {
        do { try verify() }
        catch { fputs("FAIL: \(error)\n", stderr); exit(1) }
    }

    static func verify() throws {
        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let png = fixture()
        let result = try run(png)
        try png.write(to: directory.appendingPathComponent("fixture.png"))
        try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("recognition.json"))
        try require(result["error"] == nil, "Generated fixture must be recognized")
        try require(result["imageWidth"] as? Int == 1000 && result["imageHeight"] as? Int == 600, "Original dimensions must survive")
        let lines = result["lines"] as! [[String: Any]]
        let words = lines.flatMap { $0["words"] as! [[String: Any]] }
        let text = (result["text"] as! String).replacingOccurrences(of: " ", with: "")
        // Validate stable anchor words, digits and Chinese, not every spelling
        // choice made by different OS recognition models (e.g. words / Woras).
        try require(text.contains("TBOXOCR") && text.contains("Shared") && text.contains("123") && text.contains("中文识别"), text)
        for word in words {
            let left = word["left"] as! Double, top = word["top"] as! Double
            let width = word["width"] as! Double, height = word["height"] as! Double
            try require(left >= 0 && top >= 0 && width > 0 && height > 0 && left + width <= 1.001 && top + height <= 1.001,
                        "Words must have usable normalized bounds")
        }
        guard let first = words.first(where: { $0["text"] as? String == "TBOX" }),
              let second = words.first(where: { $0["text"] as? String == "Shared" }) else { throw Failure.invalid("Missing English word boxes") }
        try require(abs((first["left"] as! Double) * 1000 - 60) < 20, "Word box must map to original image pixels")
        try require((first["top"] as! Double) < (second["top"] as! Double), "Coordinates must use a top-left origin")
        try require(words.contains { ($0["text"] as! String).unicodeScalars.contains { $0.value >= 0x4E00 && $0.value <= 0x9FFF } },
                     "Chinese word positions must be retained")
        let empty = try run(fixture(blank: true))
        try require(empty["text"] as? String == "" && (empty["lines"] as! [Any]).isEmpty, "Blank image must return no text")
        let invalid = try run(Data("invalid image".utf8))
        try require(invalid["error"] is String, "Invalid images must return a recoverable error")
        let report: [String: Any] = ["success": true, "lineCount": lines.count, "wordCount": words.count,
            "originalImageCoordinates": true, "chineseAndEnglish": true, "blankImage": true, "invalidImage": true]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("result.json"))
        print("PASS: native OCR metadata, Chinese/English, original coordinates, blank and invalid images (\(words.count) words)")
    }
}
