using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using LineTrans.Core;
using LineTrans.Core.Dictionary;

namespace LineTrans.App.Services;

/// <summary>
/// 把 Core 的 <see cref="AiClient"/> 接到 Dictionary 的 <see cref="IAiExplainProvider"/> 上，
/// 让划词查义在本地词库未收录时能用同一个模型兜底（与安卓端 / 网页端行为一致）。
///
/// 注意：Dictionary 类库本身不联网，AI 兜底完全由这里注入；没有配置模型时
/// AiClient 会抛中文 InvalidOperationException，DictionaryService 会吞掉它并继续返回本地结果。
/// </summary>
public sealed class AiExplainProvider : IAiExplainProvider
{
    private readonly Func<AiClient?> _client;

    public AiExplainProvider(Func<AiClient?> client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    private AiClient Require()
        => _client() ?? throw new InvalidOperationException("AI 客户端不可用：请先在「设置」里配置模型");

    /// <inheritdoc />
    public async Task<string?> ExplainWordAsync(string word, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var result = await client.ChatAsync(
            "你是简明英汉词典。只输出中文释义，不要音标，不要例句，不要任何解释性文字。",
            "解释单词：" + word,
            cancellationToken).ConfigureAwait(false);
        return Clean(result.Text);
    }

    /// <inheritdoc />
    public async Task<string?> ExplainShortAsync(
        string word,
        IReadOnlyList<string> definitions,
        CancellationToken cancellationToken = default)
    {
        var client = Require();
        string joined = definitions == null ? string.Empty : string.Join("；", definitions);
        var result = await client.ChatAsync(
            "你是英汉词典编辑。把给出的英文释义压缩成一行简短中文，只输出中文，不要解释。",
            "单词：" + word + "\n英文释义：" + joined,
            cancellationToken).ConfigureAwait(false);
        return Clean(result.Text);
    }

    /// <inheritdoc />
    public async Task<DictEntry?> EntryAsync(string word, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var result = await client.ChatAsync(
            "你是英汉词典。按「词性. 中文释义」的格式，每行一条，最多 4 行。" +
            "不要音标，不要例句，不要序号，不要多余说明。",
            "查询单词：" + word,
            cancellationToken).ConfigureAwait(false);

        string text = Clean(result.Text) ?? string.Empty;
        if (text.Length == 0) return null;

        return new DictEntry
        {
            Word = word,
            Phonetic = null,
            Senses = DictionaryService.SplitSenses(text, 4),
            Source = DictionarySource.Label(DictionarySource.Ai),
            Match = LookupMatchKind.None,
        };
    }

    private static string? Clean(string? text)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
