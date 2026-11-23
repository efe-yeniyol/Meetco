import Foundation

/// Cleans transcription output and removes well-known speech-to-text hallucinations
/// that appear when a chunk contains music, noise or near-silence.
enum TranscriptTextFilter {
    static let hallucinations: Set<String> = [
        "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching",
        "please subscribe", "subscribe", "like and subscribe", "bye", "you", "so", "okay", "uh", "um",
        "altyazi m k", "altyazi", "izlediginiz icin tesekkurler", "abone olmayi unutmayin",
        "tesekkurler", "tesekkur ederim", "music", "applause", "silence",
        // Subtitle credits and sign-offs that speech models invent on silence in other languages
        // (compared after `TextSimilarity.normalized`: lower case, no accents or punctuation).
        "sous titres realises par la communaute d amara org", "sous titrage st 501", "merci d avoir regarde",
        "untertitel im auftrag des zdf fur funk 2017", "untertitel der amara org community", "untertitelung des zdf 2020",
        "danke furs zuschauen", "vielen dank furs zuschauen",
        "subtitulos realizados por la comunidad de amara org", "gracias por ver el video", "gracias por ver",
        "sottotitoli creati dalla comunita amara org", "grazie per la visione",
        "legendas pela comunidade amara org", "obrigado por assistir",
        "ondertitels ingediend door de amara org gemeenschap", "bedankt voor het kijken",
        "продолжение следует", "субтитры сделал dimatorzok", "спасибо за просмотр",
        "ご視聴ありがとうございました", "チャンネル登録お願いします",
        "请不吝点赞 订阅 转发 打赏支持明镜与点点栏目", "谢谢观看", "字幕由amara org社区提供",
        "시청해 주셔서 감사합니다", "구독과 좋아요 부탁드립니다",
        "شكرا للمشاهدة", "اشترك في القناة",
    ]

    static func clean(_ text: String) -> String {
        var result = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Remove bracketed sound annotations like "[Music]" or "(applause)".
        result = result.replacingOccurrences(of: #"[\[\(][^\]\)]{0,40}[\]\)]"#, with: "", options: .regularExpression)
        result = result.replacingOccurrences(of: #"\s{2,}"#, with: " ", options: .regularExpression)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func isLikelyHallucination(_ text: String) -> Bool {
        let normalized = TextSimilarity.normalized(text)
        if normalized.isEmpty { return true }
        if hallucinations.contains(normalized) { return true }
        // Single repeated token like "you you you you".
        let words = normalized.split(separator: " ")
        if words.count >= 4 && Set(words).count == 1 { return true }
        return false
    }
}
