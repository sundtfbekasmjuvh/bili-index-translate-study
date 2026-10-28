using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App.Popup;

/// <summary>
/// Ephemeral, non-activating companion floating window with editorial dictionary hierarchy,
/// surgical SSE progressive rendering, 0.7x slow shadowing TTS, clickable collocation speech,
/// context-highlighted examples, compact/detailed view toggle, and one-click study note copy.
/// </summary>
public partial class StudyCardWindow : Window, IPopupSurface
{
    public const double ShadowMarginLogical = 14;

    private readonly SpeechPlayer _speech = new();
    private readonly HashSet<int> _revealedIndices = new();
    private readonly bool _animations = SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    private StudyDeck? _deck;
    private string? _model;
    private RequestRoute? _route;
    private bool _isCached;
    private int _cardIndex;
    private bool _quizMode;
    private bool _compactMode;
    private double _textScale = 1.0;
    private string _renderedCardKey = string.Empty;

    private bool _dragging;
    private Point _dragStart;
    private PxPoint _dragOrigin;
    private string? _captionBeforeFlash;

    public StudyCardWindow()
    {
        InitializeComponent();
        HeaderBar.MouseLeftButtonDown += OnHeaderMouseDown;
        HeaderBar.MouseMove += OnHeaderMouseMove;
        HeaderBar.MouseLeftButtonUp += OnHeaderMouseUp;
    }

    public event Action? CloseRequested;
    public event Action? RetryRequested;
    public event Action? SpeedToggleRequested;
    public event Action? Moved;

    public IntPtr Hwnd { get; private set; }
    public string? CurrentSource { get; private set; }
    public bool HasBeenDragged { get; private set; }
    public bool PreferFastModel { get; private set; }

    public void EnsureHandle()
    {
        if (Hwnd != IntPtr.Zero) return;
        var helper = new WindowInteropHelper(this);
        Hwnd = helper.EnsureHandle();
        ApplyNonActivatingStyles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        ApplyNonActivatingStyles();
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    private void ApplyNonActivatingStyles()
    {
        if (Hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtrW(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtrW(Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // ---- Ephemeral Content States -----------------------------------------------------------

    public void ShowLoading(string sourceText, bool preserveDrag = false)
    {
        _speech.Stop();
        CurrentSource = sourceText;
        _deck = null;
        _model = null;
        _route = null;
        _isCached = false;
        _cardIndex = 0;
        _quizMode = false;
        _revealedIndices.Clear();
        _renderedCardKey = string.Empty;
        if (!preserveDrag) HasBeenDragged = false;

        SyncHeaderPills();
        LangBadge.Visibility = Visibility.Collapsed;
        QuizButton.Visibility = Visibility.Collapsed;
        CopyCardButton.Visibility = Visibility.Collapsed;
        QuizButton.Content = "自测";

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DeckPanel.Visibility = Visibility.Collapsed;
        Caption.Text = PreferFastModel ? "⚡ 2B 极速拆解中…" : "流式拆解中…";
        _captionBeforeFlash = null;
        StartLoadingPulse();
    }

    public void ShowPartialDeck(string sourceText, StudyDeckResult partialResult)
    {
        StopLoadingPulse();
        CurrentSource = sourceText;
        _deck = partialResult.Deck;
        _model = partialResult.Model;
        _route = partialResult.Route;
        _isCached = false;

        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DeckPanel.Visibility = Visibility.Visible;

        RenderCurrentCard(forceFullRebuild: false);
    }

    public void ShowDeck(string sourceText, StudyDeckResult result)
    {
        StopLoadingPulse();
        CurrentSource = sourceText;
        _deck = result.Deck;
        _model = result.Model;
        _route = result.Route;
        _isCached = result.IsCached;

        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DeckPanel.Visibility = Visibility.Visible;

        RenderCurrentCard(forceFullRebuild: false);
    }

    public void ShowError(string sourceText, TranslationError error)
    {
        StopLoadingPulse();
        _speech.Stop();
        CurrentSource = sourceText;
        _deck = null;
        _renderedCardKey = string.Empty;

        LangBadge.Visibility = Visibility.Collapsed;
        QuizButton.Visibility = Visibility.Collapsed;
        CopyCardButton.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        DeckPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;

        ErrorTitle.Text = error.Kind switch
        {
            TranslationErrorKind.Timeout => "响应超时",
            TranslationErrorKind.Network => "连接异常",
            TranslationErrorKind.Configuration => "请检查服务配置",
            _ => "学习卡片生成失败",
        };
        ErrorMessage.Text = error.Message;
        ErrorHint.Text = error.Hint ?? string.Empty;
        ErrorHint.Visibility = string.IsNullOrEmpty(error.Hint) ? Visibility.Collapsed : Visibility.Visible;
        Caption.Text = error.Route == RequestRoute.Relay ? "relay" : string.Empty;
    }

    public void ClearState()
    {
        StopLoadingPulse();
        _speech.Stop();
        CurrentSource = null;
        _deck = null;
        _model = null;
        _route = null;
        _isCached = false;
        _cardIndex = 0;
        _quizMode = false;
        _revealedIndices.Clear();
        _renderedCardKey = string.Empty;
        HasBeenDragged = false;
        ChipPanel.Children.Clear();
        CollocationPanel.Children.Clear();
        ExampleSourceText.Inlines.Clear();
        Caption.Text = string.Empty;
        _captionBeforeFlash = null;
    }

    private void SyncHeaderPills()
    {
        var accent = (Brush)FindResource("Popup.Accent");
        var secondary = (Brush)FindResource("Popup.TextSecondary");
        var border = (Brush)FindResource("Popup.Border");

        CompactButton.Content = _compactMode ? "精简" : "详尽";
        CompactButton.Foreground = _compactMode ? accent : secondary;
        CompactButton.BorderBrush = _compactMode ? accent : border;

        SpeedButton.Content = PreferFastModel ? "⚡2B" : "9B";
        SpeedButton.Foreground = PreferFastModel ? accent : secondary;
        SpeedButton.BorderBrush = PreferFastModel ? accent : border;
    }

    private void RenderNavigationChips()
    {
        if (_deck is null || _deck.Cards.Count <= 1)
        {
            NavBorder.Visibility = Visibility.Collapsed;
            ChipPanel.Children.Clear();
            return;
        }

        ChipPanel.Children.Clear();
        var pillStyle = (Style)FindResource("StudyPillButton");
        var accentBrush = (Brush)FindResource("Popup.Accent");
        var accentSoftBrush = (Brush)FindResource("Popup.AccentSoft");

        for (var i = 0; i < _deck.Cards.Count; i++)
        {
            var idx = i;
            var c = _deck.Cards[i];
            var btn = new Button
            {
                Style = pillStyle,
                Content = $"{i + 1}. {c.Term}",
                Margin = new Thickness(0, 0, 4, 4),
                MaxWidth = 136,
            };
            if (i == _cardIndex)
            {
                btn.Background = accentSoftBrush;
                btn.BorderBrush = accentBrush;
                btn.Foreground = accentBrush;
            }
            btn.Click += (_, _) =>
            {
                if (_cardIndex == idx) return;
                _speech.Stop();
                _cardIndex = idx;
                BodyScroll.ScrollToTop();
                RenderCurrentCard(forceFullRebuild: true);
            };
            ChipPanel.Children.Add(btn);
        }
        NavBorder.Visibility = Visibility.Visible;
    }

    private void UpdateFooterCaption()
    {
        if (_deck is null) return;
        var parts = new List<string>(4);
        if (_deck.Cards.Count > 1) parts.Add($"{_cardIndex + 1}/{_deck.Cards.Count}");
        if (!string.IsNullOrWhiteSpace(_model)) parts.Add(_model.Replace("Index-Translate-", string.Empty));
        if (_deck.IsStreaming) parts.Add("加载其余词卡…");
        else if (_isCached) parts.Add("⚡瞬时");
        else if (_route is { } r) parts.Add(r == RequestRoute.Relay ? "relay" : "direct");
        Caption.Text = string.Join(" · ", parts);
        _captionBeforeFlash = null;
    }

    private void RenderCurrentCard(bool forceFullRebuild)
    {
        if (_deck is null) return;

        LangBadgeText.Text = _deck.SourceLanguage.ToUpperInvariant();
        LangBadge.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(_deck.GrammarNote))
        {
            GrammarText.Inlines.Clear();
            GrammarText.Inlines.Add(new Run("句法  ") { FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Popup.Accent") });
            GrammarText.Inlines.Add(new Run(_deck.GrammarNote));
            GrammarBorder.Visibility = Visibility.Visible;
        }
        else
        {
            GrammarBorder.Visibility = Visibility.Collapsed;
        }

        if (_deck.Cards.Count == 0)
        {
            CardItemArea.Visibility = Visibility.Collapsed;
            StreamingFirstCardHint.Visibility = Visibility.Visible;
            QuizButton.Visibility = Visibility.Collapsed;
            CopyCardButton.Visibility = Visibility.Collapsed;
            UpdateFooterCaption();
            return;
        }

        StreamingFirstCardHint.Visibility = Visibility.Collapsed;
        CardItemArea.Visibility = Visibility.Visible;
        QuizButton.Visibility = Visibility.Visible;
        CopyCardButton.Visibility = Visibility.Visible;

        _cardIndex = Math.Clamp(_cardIndex, 0, _deck.Cards.Count - 1);
        var card = _deck.Cards[_cardIndex];
        var nextKey = $"{_cardIndex}:{card.Term}:{card.Example?.Source}:{_textScale:F2}";

        // Surgical update: if active card hasn't changed while subsequent SSE cards stream in,
        // update ONLY the navigation chips and footer caption — zero layout thrash!
        if (!forceFullRebuild && _renderedCardKey == nextKey)
        {
            RenderNavigationChips();
            UpdateFooterCaption();
            return;
        }
        _renderedCardKey = nextKey;

        var masked = _quizMode && !_revealedIndices.Contains(_cardIndex);
        RenderNavigationChips();

        // Hero Headword & Phonetics
        TermText.Text = card.Term;
        TermText.FontSize = Math.Round(19 * _textScale);

        if (!string.IsNullOrWhiteSpace(card.Surface))
        {
            SurfaceText.Text = "文中: " + card.Surface;
            SurfaceBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SurfaceBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(card.PartOfSpeech))
        {
            PosText.Text = card.PartOfSpeech;
            PosBadge.Visibility = Visibility.Visible;
        }
        else
        {
            PosBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(card.Register))
        {
            RegisterText.Text = card.Register;
            RegisterBadge.Visibility = Visibility.Visible;
        }
        else
        {
            RegisterBadge.Visibility = Visibility.Collapsed;
        }

        ReadingText.Text = card.Reading;
        ReadingText.FontSize = Math.Round(12.5 * _textScale);
        ReadingText.Visibility = string.IsNullOrWhiteSpace(card.Reading) ? Visibility.Collapsed : Visibility.Visible;
        MetaPanel.Visibility = (PosBadge.Visibility == Visibility.Visible
                                || RegisterBadge.Visibility == Visibility.Visible
                                || ReadingText.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Core Definition / Self-Test Mask
        MeaningText.Text = card.Meaning;
        MeaningText.FontSize = Math.Round(14.5 * _textScale);
        if (masked)
        {
            MeaningText.Visibility = Visibility.Collapsed;
            RevealMaskButton.Visibility = Visibility.Visible;
        }
        else
        {
            RevealMaskButton.Visibility = Visibility.Collapsed;
            MeaningText.Visibility = string.IsNullOrWhiteSpace(card.Meaning) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Editorial Hairline Insights (hidden in Compact Mode or Masked Self-Test)
        InsightsPanel.Visibility = (!_compactMode && !masked) ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(card.Nuance))
        {
            NuanceText.Text = card.Nuance;
            NuanceText.FontSize = Math.Round(12 * _textScale);
            NuanceRow.Visibility = Visibility.Visible;
        }
        else
        {
            NuanceRow.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(card.MemoryAid))
        {
            MemoryText.Text = card.MemoryAid;
            MemoryText.FontSize = Math.Round(12 * _textScale);
            MemoryRow.Visibility = Visibility.Visible;
        }
        else
        {
            MemoryRow.Visibility = Visibility.Collapsed;
        }

        CollocationPanel.Children.Clear();
        if (card.Collocations.Count > 0)
        {
            var pillStyle = (Style)FindResource("StudyPillButton");
            var fg = (Brush)FindResource("Popup.Text");
            foreach (var col in card.Collocations)
            {
                var phrase = ExtractForeignPhrase(col);
                var btn = new Button
                {
                    Style = pillStyle,
                    Foreground = fg,
                    Padding = new Thickness(7, 2.5, 7, 2.5),
                    Margin = new Thickness(0, 0, 5, 4),
                    FontSize = Math.Round(11.5 * _textScale),
                    FontWeight = FontWeights.Normal,
                    ToolTip = "点击朗读该搭配短语",
                    Content = new TextBlock
                    {
                        Text = col,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300,
                    },
                };
                btn.Click += (_, _) =>
                {
                    if (!_speech.TrySpeak(phrase, _deck?.SourceLanguage))
                        FlashCaption("系统未检测到可用语音引擎");
                };
                CollocationPanel.Children.Add(btn);
            }
            CollocationRow.Visibility = Visibility.Visible;
        }
        else
        {
            CollocationRow.Visibility = Visibility.Collapsed;
        }

        // Context-Highlighted Bilingual Example
        ExampleSourceText.Inlines.Clear();
        if (card.Example is { } ex && (!string.IsNullOrWhiteSpace(ex.Source) || !string.IsNullOrWhiteSpace(ex.Target)))
        {
            ExampleSourceText.FontSize = Math.Round(12.5 * _textScale);
            var hasSrc = !string.IsNullOrWhiteSpace(ex.Source);
            if (hasSrc)
                PopulateHighlightedExample(ex.Source, card.Term, card.Surface);

            SpeakExampleButton.Visibility = hasSrc ? Visibility.Visible : Visibility.Collapsed;
            SpeakExampleSlowButton.Visibility = hasSrc ? Visibility.Visible : Visibility.Collapsed;

            if (!masked && !string.IsNullOrWhiteSpace(ex.Target))
            {
                ExampleTargetText.Text = ex.Target;
                ExampleTargetText.FontSize = Math.Round(11.8 * _textScale);
                ExampleTargetText.Visibility = Visibility.Visible;
            }
            else
            {
                ExampleTargetText.Visibility = Visibility.Collapsed;
            }
            ExampleSection.Visibility = Visibility.Visible;
        }
        else
        {
            ExampleSection.Visibility = Visibility.Collapsed;
        }

        UpdateFooterCaption();
    }

    private void PopulateHighlightedExample(string sentence, string term, string surface)
    {
        var candidates = new[] { surface, term }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 2)
            .Select(s => s.Trim());

        Regex? regex = null;
        foreach (var cand in candidates)
        {
            var esc = Regex.Escape(cand);
            var pattern = Regex.IsMatch(cand, @"^[A-Za-z]{4,}$")
                ? $@"(\b{esc}\w*|\b{(cand.Length >= 6 ? Regex.Escape(cand[..^1]) : esc)}[A-Za-z]{{1,4}}\b)"
                : $"({esc})";
            var r = new Regex(pattern, RegexOptions.IgnoreCase);
            if (r.IsMatch(sentence))
            {
                regex = r;
                break;
            }
        }

        if (regex is null)
        {
            ExampleSourceText.Inlines.Add(new Run(sentence));
            return;
        }

        var hlBg = (Brush)FindResource("Popup.AccentSoft");
        var lastIdx = 0;
        foreach (Match m in regex.Matches(sentence))
        {
            if (m.Index > lastIdx)
                ExampleSourceText.Inlines.Add(new Run(sentence[lastIdx..m.Index]));
            ExampleSourceText.Inlines.Add(new Run(m.Value)
            {
                Background = hlBg,
                FontWeight = FontWeights.SemiBold,
            });
            lastIdx = m.Index + m.Length;
        }
        if (lastIdx < sentence.Length)
            ExampleSourceText.Inlines.Add(new Run(sentence[lastIdx..]));
    }

    private static string ExtractForeignPhrase(string collocation)
    {
        var s = (collocation ?? string.Empty).Trim();
        var cleaned = Regex.Replace(s, @"\s*[（(][^)）]*[）)]\s*$", string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : s;
    }

    private string FormatActiveCardMarkdown()
    {
        if (_deck is null || _deck.Cards.Count == 0) return string.Empty;
        var c = _deck.Cards[Math.Clamp(_cardIndex, 0, _deck.Cards.Count - 1)];
        var meta = string.Join(" · ", new[] { c.PartOfSpeech, c.Register }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var sb = new StringBuilder();
        sb.Append(c.Term);
        if (!string.IsNullOrWhiteSpace(c.Reading)) sb.Append(" [").Append(c.Reading).Append(']');
        if (meta.Length > 0) sb.Append(" (").Append(meta).Append(')');
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(c.Meaning)) sb.AppendLine("释义：" + c.Meaning);
        if (!string.IsNullOrWhiteSpace(c.Nuance)) sb.AppendLine("辨析：" + c.Nuance);
        if (!string.IsNullOrWhiteSpace(c.MemoryAid)) sb.AppendLine("助记：" + c.MemoryAid);
        if (c.Collocations.Count > 0) sb.AppendLine("搭配：" + string.Join(" / ", c.Collocations));
        if (c.Example is { Source.Length: > 0 } ex)
            sb.AppendLine("例句：" + ex.Source + (!string.IsNullOrWhiteSpace(ex.Target) ? " — " + ex.Target : string.Empty));
        if (!string.IsNullOrWhiteSpace(_deck.GrammarNote))
            sb.Append("句法：" + _deck.GrammarNote);
        return sb.ToString().Trim();
    }

    // ---- Sizing, Scaling & Scrolling --------------------------------------------------------

    public void SetSizeLimits(double maxCardWidth, double maxBodyHeight)
    {
        var w = Math.Clamp(maxCardWidth, 280, 404);
        Card.Width = w;
        Card.MaxWidth = w;
        BodyScroll.MaxHeight = Math.Max(140, maxBodyHeight);
    }

    public void ApplyTextScale(double scale)
    {
        _textScale = Math.Clamp(scale, 0.85, 1.45);
        if (_deck is not null) RenderCurrentCard(forceFullRebuild: true);
    }

    public void ScrollBy(int wheelDelta)
    {
        var step = -wheelDelta / 120.0 * 42;
        BodyScroll.ScrollToVerticalOffset(BodyScroll.VerticalOffset + step);
    }

    public void FadeIn()
    {
        if (_animations)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else
            Opacity = 1;
    }

    public void ResetOpacity()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
    }

    private void StartLoadingPulse()
    {
        if (!_animations) return;
        var pulse = new DoubleAnimation(0.4, 0.9, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LoadingPanel.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopLoadingPulse()
    {
        LoadingPanel.BeginAnimation(OpacityProperty, null);
        LoadingPanel.Opacity = 1;
    }

    // ---- Button & Drag Handlers -------------------------------------------------------------

    private void OnCompactClick(object sender, RoutedEventArgs e)
    {
        _compactMode = !_compactMode;
        SyncHeaderPills();
        if (_deck is not null)
            InsightsPanel.Visibility = (!_compactMode && !(_quizMode && !_revealedIndices.Contains(_cardIndex)))
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        PreferFastModel = !PreferFastModel;
        SyncHeaderPills();
        SpeedToggleRequested?.Invoke();
    }

    private void OnQuizClick(object sender, RoutedEventArgs e)
    {
        if (_deck is null) return;
        _quizMode = !_quizMode;
        _revealedIndices.Clear();
        QuizButton.Content = _quizMode ? "退出" : "自测";
        RenderCurrentCard(forceFullRebuild: true);
    }

    private void OnRevealMaskClick(object sender, RoutedEventArgs e)
    {
        if (_deck is null) return;
        _revealedIndices.Add(_cardIndex);
        RenderCurrentCard(forceFullRebuild: true);
    }

    private void OnCopyCardClick(object sender, RoutedEventArgs e)
    {
        var text = FormatActiveCardMarkdown();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetDataObject(text, false);
            CopyCardButton.Content = "\uE73E";
            FlashCaption("✓ 已复制词卡笔记");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyCardButton.Content = "\uE8C8";
            };
            timer.Start();
        }
        catch
        {
            FlashCaption("剪贴板正忙");
        }
    }

    private void OnPrevCardClick(object sender, RoutedEventArgs e) => StepCard(-1);
    private void OnNextCardClick(object sender, RoutedEventArgs e) => StepCard(1);

    private void StepCard(int delta)
    {
        if (_deck is null || _deck.Cards.Count <= 1) return;
        _speech.Stop();
        var n = _deck.Cards.Count;
        _cardIndex = (_cardIndex + delta + n) % n;
        BodyScroll.ScrollToTop();
        RenderCurrentCard(forceFullRebuild: true);
    }

    private void OnSpeakTermClick(object sender, RoutedEventArgs e) => SpeakTerm(slow: false);
    private void OnSpeakSlowClick(object sender, RoutedEventArgs e) => SpeakTerm(slow: true);

    private void SpeakTerm(bool slow)
    {
        if (_deck is null || _deck.Cards.Count == 0) return;
        var card = _deck.Cards[Math.Clamp(_cardIndex, 0, _deck.Cards.Count - 1)];
        if (!_speech.TrySpeak(card.Term, _deck.SourceLanguage, slow))
            FlashCaption("系统未检测到可用语音引擎");
        else if (slow)
            FlashCaption("0.7x 慢读跟读中…");
    }

    private void OnSpeakExampleClick(object sender, RoutedEventArgs e) => SpeakExample(slow: false);
    private void OnSpeakExampleSlowClick(object sender, RoutedEventArgs e) => SpeakExample(slow: true);

    private void SpeakExample(bool slow)
    {
        if (_deck is null || _deck.Cards.Count == 0) return;
        var card = _deck.Cards[Math.Clamp(_cardIndex, 0, _deck.Cards.Count - 1)];
        if (card.Example is not { } ex) return;
        if (!_speech.TrySpeak(ex.Source, _deck.SourceLanguage, slow))
            FlashCaption("系统未检测到可用语音引擎");
        else if (slow)
            FlashCaption("0.7x 慢读例句中…");
    }

    private void FlashCaption(string message)
    {
        _captionBeforeFlash ??= Caption.Text;
        Caption.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Caption.Text = _captionBeforeFlash ?? string.Empty;
            _captionBeforeFlash = null;
        };
        timer.Start();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is not null) return;
        _dragging = true;
        HasBeenDragged = true;
        _dragStart = PointToScreenPhysical(e.GetPosition(this));
        NativeMethods.GetWindowRect(Hwnd, out var r);
        _dragOrigin = new PxPoint(r.Left, r.Top);
        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = PointToScreenPhysical(e.GetPosition(this));
        var x = _dragOrigin.X + (int)(now.X - _dragStart.X);
        var y = _dragOrigin.Y + (int)(now.Y - _dragStart.Y);
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        HeaderBar.ReleaseMouseCapture();
        Moved?.Invoke();
    }

    private Point PointToScreenPhysical(Point logical)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return logical;
        return PointToScreen(logical);
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T t) return t;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    public PxRect? PhysicalRect()
    {
        if (Hwnd == IntPtr.Zero || !IsVisible || !NativeMethods.GetWindowRect(Hwnd, out var r)) return null;
        return new PxRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    public PxRect? CardPhysicalRect()
    {
        var w = PhysicalRect();
        if (w is not { } rect) return null;
        var inset = (int)Math.Round(ShadowMarginLogical * DpiScale());
        return new PxRect(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
    }

    public double DpiScale()
    {
        if (Hwnd != IntPtr.Zero)
        {
            var dpi = NativeMethods.GetDpiForWindow(Hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    protected override void OnClosed(EventArgs e)
    {
        _speech.Dispose();
        base.OnClosed(e);
    }
}
