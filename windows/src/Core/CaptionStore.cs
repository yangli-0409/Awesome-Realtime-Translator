using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AwesomeRealtimeTranslator.Core;

/// <summary>一行字幕。原文定稿后不变，译文回填一次之后也不再改写。</summary>
public sealed class CaptionLine : INotifyPropertyChanged
{
    private string? _translation;
    private bool _translationFailed;

    public CaptionLine(Guid id, string source, double startedAt, DateTimeOffset wallClock)
    {
        Id = id;
        Source = source;
        StartedAt = startedAt;
        WallClock = wallClock;
    }

    public Guid Id { get; }

    public string Source { get; }

    public double StartedAt { get; }

    public DateTimeOffset WallClock { get; }

    public string? Translation
    {
        get => _translation;
        set
        {
            if (_translation == value)
            {
                return;
            }

            _translation = value;
            Raise();
            Raise(nameof(IsTranslating));
        }
    }

    public bool TranslationFailed
    {
        get => _translationFailed;
        set
        {
            if (_translationFailed == value)
            {
                return;
            }

            _translationFailed = value;
            Raise();
            Raise(nameof(IsTranslating));
        }
    }

    public bool IsTranslating => _translation is null && !_translationFailed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 字幕状态。单一数据源，悬浮窗和导出都从这里读。
///
/// 不是线程安全的：所有写入都必须在 UI 线程上。管道里的后台任务通过
/// <see cref="SynchronizationContext"/> 回到 UI 线程再调这里，和 macOS 版
/// 把一切放在 MainActor 上是同一个做法。
/// </summary>
public sealed class CaptionStore : INotifyPropertyChanged
{
    private const int MaxLines = 2000;

    private string _pendingSource = string.Empty;
    private string? _statusMessage;
    private bool _isRunning;

    /// <summary>已定稿的句子，按时间升序</summary>
    public ObservableCollection<CaptionLine> Lines { get; } = [];

    public DateTimeOffset? SessionStartedAt { get; private set; }

    /// <summary>未定稿的尾句，只显示在原文区末尾，永不翻译</summary>
    public string PendingSource
    {
        get => _pendingSource;
        set
        {
            if (_pendingSource == value)
            {
                return;
            }

            _pendingSource = value;
            Raise();
        }
    }

    /// <summary>给用户看的一句话状态（权限、下载、降级提示）</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            Raise();
        }
    }

    /// <summary>悬浮窗要显示运行状态，放在同一个对象里，省掉一层状态同步</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            Raise();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void StartSession()
    {
        Lines.Clear();
        PendingSource = string.Empty;
        SessionStartedAt = DateTimeOffset.Now;
    }

    public void Append(Sentence sentence)
    {
        Lines.Add(new CaptionLine(sentence.Id, sentence.Text, sentence.StartedAt, DateTimeOffset.Now));
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    public void SetTranslation(string text, Guid id)
    {
        CaptionLine? line = Find(id);
        if (line is null)
        {
            return;
        }

        line.Translation = text;
        line.TranslationFailed = false;
    }

    public void MarkTranslationFailed(Guid id)
    {
        CaptionLine? line = Find(id);
        if (line is not null)
        {
            line.TranslationFailed = true;
        }
    }

    private CaptionLine? Find(Guid id)
    {
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            if (Lines[i].Id == id)
            {
                return Lines[i];
            }
        }

        return null;
    }

    /// <summary>最近 n 条，供悬浮窗显示</summary>
    public IReadOnlyList<CaptionLine> Tail(int n)
    {
        int take = Math.Max(1, n);
        int skip = Math.Max(0, Lines.Count - take);
        return Lines.Skip(skip).ToList();
    }

    /// <summary>最近几句原文，给需要上下文的翻译引擎用</summary>
    public IReadOnlyList<string> RecentContext(int n)
    {
        int skip = Math.Max(0, Lines.Count - n);
        return Lines.Skip(skip).Select(l => l.Source).ToList();
    }

    // MARK: - 导出

    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (CaptionLine line in Lines)
            {
                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }

                sb.Append('[').Append(Stamp(line.StartedAt)).Append("] ").Append(line.Source);
                if (!string.IsNullOrEmpty(line.Translation))
                {
                    sb.Append('\n').Append(line.Translation);
                }
            }

            return sb.ToString();
        }
    }

    public string Markdown
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("# 面试记录");
            if (SessionStartedAt is { } started)
            {
                sb.Append(' ').Append(started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            sb.Append("\n\n");

            bool first = true;
            foreach (CaptionLine line in Lines)
            {
                if (!first)
                {
                    sb.Append("\n\n");
                }

                first = false;
                sb.Append("**[").Append(Stamp(line.StartedAt)).Append("]** ").Append(line.Source);
                if (!string.IsNullOrEmpty(line.Translation))
                {
                    sb.Append("\n\n> ").Append(line.Translation);
                }
            }

            sb.Append('\n');
            return sb.ToString();
        }
    }

    private static string Stamp(double seconds)
    {
        int total = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{total % 60:00}");
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
