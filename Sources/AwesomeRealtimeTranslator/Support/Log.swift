import OSLog

enum Log {
    private static let subsystem = "com.leon.AwesomeRealtimeTranslator"
    static let audio = Logger(subsystem: subsystem, category: "audio")
    static let speech = Logger(subsystem: subsystem, category: "speech")
    static let translate = Logger(subsystem: subsystem, category: "translate")
    static let ui = Logger(subsystem: subsystem, category: "ui")
    static let session = Logger(subsystem: subsystem, category: "session")
}
