#!/usr/bin/env swift
// 生成 app 图标的 1024×1024 母图。
//
// 为什么用代码画而不是放一张图：图标是设计产物，但这个项目里它也是**可复现**的东西 ——
// 改配色、改比例只要改常量重跑，不用找原始设计文件。
//
//   swift Tools/MakeAppIcon.swift <输出路径.png>
//   Tools/make-icon.sh          # 连带切出全部尺寸并打包成 .icns
//
// 设计意图：深色方块上两条字幕条，上面白色（原文）、下面绿色（译文），
// 绿色和字幕窗「正在听」的状态点同色。16px 下仍然能看出是「两行字幕」。

import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let side: CGFloat = 1024
// macOS 图标不铺满画布，四周留白由系统网格决定
let inset: CGFloat = 100
let cornerRadius: CGFloat = 185

let backgroundTop = CGColor(red: 0.17, green: 0.23, blue: 0.29, alpha: 1)
let backgroundBottom = CGColor(red: 0.07, green: 0.09, blue: 0.12, alpha: 1)
let sourceBar = CGColor(red: 1, green: 1, blue: 1, alpha: 0.92)
let translationBar = CGColor(red: 0.24, green: 0.86, blue: 0.59, alpha: 1)
let hintBar = CGColor(red: 1, green: 1, blue: 1, alpha: 0.28)

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("用法：swift MakeAppIcon.swift <输出路径.png>\n".utf8))
    exit(2)
}
let outputURL = URL(fileURLWithPath: CommandLine.arguments[1])

guard let context = CGContext(
    data: nil,
    width: Int(side),
    height: Int(side),
    bitsPerComponent: 8,
    bytesPerRow: 0,
    space: CGColorSpace(name: CGColorSpace.sRGB)!,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else {
    FileHandle.standardError.write(Data("无法创建位图上下文\n".utf8))
    exit(1)
}

let plate = CGRect(x: inset, y: inset, width: side - inset * 2, height: side - inset * 2)
let platePath = CGPath(
    roundedRect: plate,
    cornerWidth: cornerRadius,
    cornerHeight: cornerRadius,
    transform: nil
)

// 底板渐变
context.saveGState()
context.addPath(platePath)
context.clip()
let gradient = CGGradient(
    colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
    colors: [backgroundTop, backgroundBottom] as CFArray,
    locations: [0, 1]
)!
context.drawLinearGradient(
    gradient,
    start: CGPoint(x: 0, y: plate.maxY),
    end: CGPoint(x: 0, y: plate.minY),
    options: []
)
context.restoreGState()

// 顶部一道高光，让底板不显得死板
context.saveGState()
context.addPath(platePath)
context.setStrokeColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.14))
context.setLineWidth(6)
context.strokePath()
context.restoreGState()

/// 画一条居中的字幕条
func bar(widthRatio: CGFloat, centerY: CGFloat, height: CGFloat, color: CGColor) {
    let width = plate.width * widthRatio
    let rect = CGRect(
        x: plate.midX - width / 2,
        y: centerY - height / 2,
        width: width,
        height: height
    )
    context.addPath(CGPath(
        roundedRect: rect,
        cornerWidth: height / 2,
        cornerHeight: height / 2,
        transform: nil
    ))
    context.setFillColor(color)
    context.fillPath()
}

let barHeight = plate.height * 0.105
// 最上面一条半透明短条：暗示上面还有已经滚过去的字幕
bar(widthRatio: 0.42, centerY: plate.midY + plate.height * 0.26, height: barHeight * 0.72, color: hintBar)
bar(widthRatio: 0.70, centerY: plate.midY + plate.height * 0.06, height: barHeight, color: sourceBar)
bar(widthRatio: 0.56, centerY: plate.midY - plate.height * 0.17, height: barHeight, color: translationBar)

guard let image = context.makeImage(),
      let destination = CGImageDestinationCreateWithURL(
          outputURL as CFURL,
          UTType.png.identifier as CFString,
          1,
          nil
      )
else {
    FileHandle.standardError.write(Data("无法写出 PNG\n".utf8))
    exit(1)
}
CGImageDestinationAddImage(destination, image, nil)
guard CGImageDestinationFinalize(destination) else {
    FileHandle.standardError.write(Data("PNG 写出失败\n".utf8))
    exit(1)
}
print("已生成 \(outputURL.path)")
