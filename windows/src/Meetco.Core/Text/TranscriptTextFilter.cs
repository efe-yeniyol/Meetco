using System.Text.RegularExpressions;

namespace Meetco.Core.Text;

/// <summary>
/// Cleans transcription output and removes well-known speech-to-text hallucinations
/// that appear when a chunk contains music, noise or near-silence.
/// </summary>
public static partial class TranscriptTextFilter
{
    /// <summary>Compared after <see cref="TextSimilarity.Normalized"/> (lower case, no accents or punctuation).</summary>
    public static readonly HashSet<string> Hallucinations = new string[]
    {
        "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching",
        "please subscribe", "subscribe", "like and subscribe", "bye", "you", "so", "okay", "uh", "um",
        "altyazi m k", "altyazi", "izlediginiz icin tesekkurler", "abone olmayi unutmayin",
        "tesekkurler", "tesekkur ederim", "music", "applause", "silence",
        // Subtitle credits and sign-offs that speech models invent on silence in other languages.
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
    }.Select(TextSimilarity.Normalized).ToHashSet();

    [GeneratedRegex(@"[\[\(][^\]\)]{0,40}[\]\)]")]
    private static partial Regex BracketedAnnotation();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();

    public static string Clean(string text)
    {
        var result = text.Trim();
        // Remove bracketed sound annotations like "[Music]" or "(applause)".
        result = BracketedAnnotation().Replace(result, "");
        result = RepeatedWhitespace().Replace(result, " ");
        return result.Trim();
    }

    public static bool IsLikelyHallucination(string text)
    {
        var normalized = TextSimilarity.Normalized(text);
        if (normalized.Length == 0) return true;
        if (Hallucinations.Contains(normalized)) return true;
        // Single repeated token like "you you you you".
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 4 && words.Distinct().Count() == 1;
    }
}
